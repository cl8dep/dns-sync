using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DnsSync.Providers.Route53;

/// <summary>
/// Amazon Route 53 DNS provider using the Route 53 REST API.
/// Authenticates via AWS Signature V4 using access key + secret, or environment variables
/// (AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_SESSION_TOKEN).
/// Writes are batched into a single ChangeResourceRecordSets call per ApplyPlanAsync invocation.
/// </summary>
public class Route53Provider : IProvider
{
    private readonly string _accessKeyId;
    private readonly string _secretAccessKey;
    private readonly string? _sessionToken;
    private readonly string _region;
    private readonly string? _configuredZoneId;
    private readonly ILogger<Route53Provider> _logger;
    private readonly HttpClient _http;

    private const string BaseUrl = "https://route53.amazonaws.com/2013-04-01";
    private const string Service = "route53";
    private const string Host = "route53.amazonaws.com";
    private static readonly XNamespace R53Ns = "https://route53.amazonaws.com/doc/2013-04-01/";

    public Route53Provider(
        string accessKeyId,
        string secretAccessKey,
        string region,
        ILogger<Route53Provider> logger,
        string? sessionToken = null,
        string? hostedZoneId = null)
        : this(accessKeyId, secretAccessKey, region, logger,
               new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, sessionToken, hostedZoneId)
    { }

    internal Route53Provider(
        string accessKeyId,
        string secretAccessKey,
        string region,
        ILogger<Route53Provider> logger,
        HttpClient http,
        string? sessionToken = null,
        string? hostedZoneId = null)
    {
        _accessKeyId = accessKeyId;
        _secretAccessKey = secretAccessKey;
        _sessionToken = sessionToken;
        _region = region;
        _configuredZoneId = hostedZoneId;
        _logger = logger;
        _http = http;
    }

    public async Task PreflightAsync(CancellationToken ct = default)
    {
        // Validate credentials by listing hosted zones (cheap, always-accessible endpoint)
        await GetAsync("/hostedzone?maxitems=1", ct);
        _logger.LogInformation("Route 53 credentials verified successfully");
    }

    public async Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default)
    {
        var zones = new List<string>();
        string? marker = null;

        while (true)
        {
            var path = marker is null
                ? "/hostedzone"
                : $"/hostedzone?marker={Uri.EscapeDataString(marker)}";

            var xml = await GetAsync(path, ct);
            var doc = XDocument.Parse(xml);

            foreach (var zone in doc.Descendants(R53Ns + "HostedZone"))
            {
                var name = zone.Element(R53Ns + "Name")?.Value;
                if (!string.IsNullOrEmpty(name))
                    zones.Add(DnsNameHelper.NormalizeZoneName(name));
            }

            var isTruncated = doc.Descendants(R53Ns + "IsTruncated").FirstOrDefault()?.Value;
            if (isTruncated != "true") break;

            marker = doc.Descendants(R53Ns + "NextMarker").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(marker)) break;
        }

        return zones;
    }

    public async Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default)
    {
        var normalized = DnsNameHelper.NormalizeZoneName(zoneName);
        var zoneId = await ResolveZoneIdAsync(normalized, ct);
        var records = await ListAllRecordsAsync(zoneId, normalized, ct);
        return new DnsZone { Name = normalized, Records = records };
    }

    public async Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default)
    {
        var normalized = DnsNameHelper.NormalizeZoneName(zoneName);
        var zoneId = await ResolveZoneIdAsync(normalized, ct);

        var changeElements = new List<XElement>();

        foreach (var change in plan.Changes)
        {
            var action = change.ChangeType switch
            {
                ChangeType.Create => "CREATE",
                ChangeType.Update => "UPSERT",
                ChangeType.Delete => "DELETE",
                _ => throw new NotSupportedException($"Unknown change type: {change.ChangeType}")
            };

            var record = (change.ChangeType == ChangeType.Delete ? change.Before : change.After)!;
            var rrsetEl = BuildResourceRecordSet(record);
            if (rrsetEl is null)
            {
                _logger.LogWarning("Skipping unsupported record type {Type} for {Name}", record.Type, record.Name);
                continue;
            }

            changeElements.Add(new XElement(R53Ns + "Change",
                new XElement(R53Ns + "Action", action),
                rrsetEl));
        }

        if (changeElements.Count == 0)
            return new ApplyResult(0, 0, []);

        // Apply each change individually so one failure does not block the rest.
        var applied = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var (changeEl, change) in changeElements.Zip(plan.Changes
            .Where(c => BuildResourceRecordSet((c.ChangeType == ChangeType.Delete ? c.Before : c.After)!) is not null)))
        {
            var body = new XDocument(
                new XElement(R53Ns + "ChangeResourceRecordSetsRequest",
                    new XElement(R53Ns + "ChangeBatch",
                        new XElement(R53Ns + "Changes", changeEl)))).ToString();
            try
            {
                await PostAsync($"/hostedzone/{zoneId}/rrset", body, ct);
                applied++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{change.ChangeType} {change.RecordName} {change.RecordType}: {ex.Message}");
                _logger.LogWarning("Failed to apply {ChangeType} {Name} {Type}: {Error}",
                    change.ChangeType, change.RecordName, change.RecordType, ex.Message);
            }
        }

        _logger.LogInformation("Applied {Applied}/{Total} change(s) to zone {ZoneId}", applied, changeElements.Count, zoneId);
        return new ApplyResult(applied, failed, errors);
    }

    // --- Zone resolution ---

    private async Task<string> ResolveZoneIdAsync(string zoneName, CancellationToken ct)
    {
        if (_configuredZoneId is not null)
        {
            // Strip /hostedzone/ prefix if present
            var id = _configuredZoneId.StartsWith("/hostedzone/")
                ? _configuredZoneId["/hostedzone/".Length..]
                : _configuredZoneId;
            _logger.LogDebug("Using configured hosted_zone_id: {ZoneId}", id);
            return id;
        }

        // ListHostedZonesByName sorts alphabetically from dnsname — the first result may not
        // be an exact match (e.g. querying "heva.co" can return "heva.health" first).
        // Paginate until we find an exact match or exhaust all zones.
        var target = DnsNameHelper.NormalizeZoneName(zoneName);
        string? nextDnsName = zoneName.TrimEnd('.');
        string? nextHostedZoneId = null;

        while (true)
        {
            var query = $"/hostedzone?dnsname={Uri.EscapeDataString(nextDnsName)}";
            if (nextHostedZoneId is not null)
                query += $"&hostedzoneid={Uri.EscapeDataString(nextHostedZoneId)}";

            var xml = await GetAsync(query, ct);
            var doc = XDocument.Parse(xml);

            foreach (var zone in doc.Descendants(R53Ns + "HostedZone"))
            {
                var candidateName = DnsNameHelper.NormalizeZoneName(zone.Element(R53Ns + "Name")?.Value ?? "");
                if (candidateName == target)
                {
                    var idPath = zone.Element(R53Ns + "Id")?.Value ?? "";
                    return idPath.StartsWith("/hostedzone/") ? idPath["/hostedzone/".Length..] : idPath;
                }
            }

            var isTruncated = doc.Descendants(R53Ns + "IsTruncated").FirstOrDefault()?.Value;
            if (isTruncated != "true") break;

            nextDnsName = doc.Descendants(R53Ns + "NextDNSName").FirstOrDefault()?.Value ?? nextDnsName;
            nextHostedZoneId = doc.Descendants(R53Ns + "NextHostedZoneId").FirstOrDefault()?.Value;
            if (nextHostedZoneId is null) break;
        }

        throw new InvalidOperationException(
            $"Zone '{zoneName}' not found in Route 53. " +
            "Ensure the hosted zone exists and credentials have access to it.");
    }

    // --- Record listing ---

    private async Task<IReadOnlyList<DnsRecord>> ListAllRecordsAsync(
        string zoneId, string zoneName, CancellationToken ct)
    {
        var records = new List<DnsRecord>();
        string? nextName = null;
        string? nextType = null;

        while (true)
        {
            string path;
            if (nextName is null)
                path = $"/hostedzone/{zoneId}/rrset?maxitems=300";
            else
                path = $"/hostedzone/{zoneId}/rrset?maxitems=300" +
                       $"&name={Uri.EscapeDataString(nextName)}" +
                       (nextType is not null ? $"&type={Uri.EscapeDataString(nextType)}" : "");

            var xml = await GetAsync(path, ct);
            var doc = XDocument.Parse(xml);

            foreach (var rrset in doc.Descendants(R53Ns + "ResourceRecordSet"))
            {
                var record = ParseResourceRecordSet(rrset);
                if (record is not null)
                    records.Add(record);
            }

            var isTruncated = doc.Descendants(R53Ns + "IsTruncated").FirstOrDefault()?.Value;
            if (isTruncated != "true") break;

            nextName = doc.Descendants(R53Ns + "NextRecordName").FirstOrDefault()?.Value;
            nextType = doc.Descendants(R53Ns + "NextRecordType").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(nextName)) break;
        }

        return records;
    }

    private static DnsRecord? ParseResourceRecordSet(XElement rrset)
    {
        var name = DnsNameHelper.NormalizeFqdn(rrset.Element(R53Ns + "Name")?.Value ?? "");
        var type = rrset.Element(R53Ns + "Type")?.Value?.ToUpperInvariant() ?? "";
        var ttlStr = rrset.Element(R53Ns + "TTL")?.Value;
        var ttl = ttlStr is not null && int.TryParse(ttlStr, out var t) ? t : 300;

        var values = rrset
            .Element(R53Ns + "ResourceRecords")?
            .Elements(R53Ns + "ResourceRecord")
            .Select(r => r.Element(R53Ns + "Value")?.Value ?? "")
            .Where(v => v.Length > 0)
            .ToList() ?? [];

        if (values.Count == 0) return null;

        return type switch
        {
            "A" => new ARecord
            {
                Name = name,
                Type = "A",
                Ttl = ttl,
                Addresses = values
            },
            "AAAA" => new AaaaRecord
            {
                Name = name,
                Type = "AAAA",
                Ttl = ttl,
                Addresses = values
            },
            "CNAME" => new CnameRecord
            {
                Name = name,
                Type = "CNAME",
                Ttl = ttl,
                Target = DnsNameHelper.NormalizeFqdn(values[0])
            },
            "MX" => new MxRecord
            {
                Name = name,
                Type = "MX",
                Ttl = ttl,
                Values = values.Select(v =>
                {
                    var parts = v.Split(' ', 2);
                    return new MxValue(
                        int.TryParse(parts[0], out var p) ? p : 10,
                        DnsNameHelper.NormalizeFqdn(parts.Length > 1 ? parts[1] : v));
                }).ToList()
            },
            "TXT" => new TxtRecord
            {
                Name = name,
                Type = "TXT",
                Ttl = ttl,
                Values = values.Select(StripTxtQuotes).ToList()
            },
            "NS" => new NsRecord
            {
                Name = name,
                Type = "NS",
                Ttl = ttl,
                Nameservers = values.Select(DnsNameHelper.NormalizeFqdn).ToList()
            },
            "CAA" => new CaaRecord
            {
                Name = name,
                Type = "CAA",
                Ttl = ttl,
                Values = values.Select(v =>
                {
                    var parts = v.Split(' ', 3);
                    return new CaaValue(
                        int.TryParse(parts[0], out var f) ? f : 0,
                        parts.Length > 1 ? parts[1] : "",
                        parts.Length > 2 ? parts[2].Trim('"') : "");
                }).ToList()
            },
            "SRV" => new SrvRecord
            {
                Name = name,
                Type = "SRV",
                Ttl = ttl,
                Values = values.Select(v =>
                {
                    var parts = v.Split(' ', 4);
                    return new SrvValue(
                        parts.Length > 0 && int.TryParse(parts[0], out var pri) ? pri : 0,
                        parts.Length > 1 && int.TryParse(parts[1], out var wt) ? wt : 0,
                        parts.Length > 2 && int.TryParse(parts[2], out var port) ? port : 0,
                        DnsNameHelper.NormalizeFqdn(parts.Length > 3 ? parts[3] : ""));
                }).ToList()
            },
            _ => null
        };
    }

    // --- Record serialization ---

    private static XElement? BuildResourceRecordSet(DnsRecord record)
    {
        var name = record.Name.TrimEnd('.');
        var ttl = record.Ttl;

        List<string>? wireValues = record switch
        {
            ARecord a => a.Addresses.ToList(),
            AaaaRecord aaaa => aaaa.Addresses.ToList(),
            CnameRecord cname => [cname.Target.TrimEnd('.')],
            MxRecord mx => mx.Values.Select(v => $"{v.Preference} {v.Exchange.TrimEnd('.')}").ToList(),
            TxtRecord txt => txt.Values.Select(v => ChunkTxt(v)).ToList(),
            NsRecord ns => ns.Nameservers.Select(n => n.TrimEnd('.')).ToList(),
            CaaRecord caa => caa.Values.Select(v => $"{v.Flags} {v.Tag} \"{v.Value}\"").ToList(),
            SrvRecord srv => srv.Values.Select(v => $"{v.Priority} {v.Weight} {v.Port} {v.Target.TrimEnd('.')}").ToList(),
            _ => null
        };

        if (wireValues is null) return null;

        return new XElement(R53Ns + "ResourceRecordSet",
            new XElement(R53Ns + "Name", name),
            new XElement(R53Ns + "Type", record.Type),
            new XElement(R53Ns + "TTL", ttl),
            new XElement(R53Ns + "ResourceRecords",
                wireValues.Select(v =>
                    new XElement(R53Ns + "ResourceRecord",
                        new XElement(R53Ns + "Value", v)))));
    }

    // --- AWS Signature V4 ---

    private async Task<string> GetAsync(string path, CancellationToken ct) =>
        await HttpRetryPolicy.ExecuteAsync(async () =>
        {
            var request = BuildRequest(HttpMethod.Get, path, "");
            return await EnsureSuccessAsync(await _http.SendAsync(request, ct));
        }, _logger, ct);

    private async Task<string> PostAsync(string path, string xmlBody, CancellationToken ct) =>
        await HttpRetryPolicy.ExecuteAsync(async () =>
        {
            var request = BuildRequest(HttpMethod.Post, path, xmlBody);
            return await EnsureSuccessAsync(await _http.SendAsync(request, ct));
        }, _logger, ct);

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, string body)
    {
        var now = DateTimeOffset.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var payloadHash = HexHash(bodyBytes);

        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Add("x-amz-date", amzDate);
        request.Headers.Add("x-amz-content-sha256", payloadHash);
        if (_sessionToken is not null)
            request.Headers.Add("x-amz-security-token", _sessionToken);

        if (body.Length > 0)
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");

        var uri = request.RequestUri!;
        var canonicalUri = uri.AbsolutePath;
        var canonicalQuery = SortQueryString(uri.Query.TrimStart('?'));

        var signedHeaders = _sessionToken is not null
            ? "host;x-amz-content-sha256;x-amz-date;x-amz-security-token"
            : "host;x-amz-content-sha256;x-amz-date";

        var canonicalHeaders = _sessionToken is not null
            ? $"host:{Host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\nx-amz-security-token:{_sessionToken}\n"
            : $"host:{Host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";

        var canonicalRequest = string.Join("\n",
            method.Method,
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var credentialScope = $"{dateStamp}/{_region}/{Service}/aws4_request";
        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            HexHash(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = GetSigningKey(dateStamp);
        var signature = HexHmac(signingKey, stringToSign);

        var authHeader =
            $"AWS4-HMAC-SHA256 Credential={_accessKeyId}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";

        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        return request;
    }

    private byte[] GetSigningKey(string dateStamp)
    {
        var kDate = HmacBytes(Encoding.UTF8.GetBytes("AWS4" + _secretAccessKey), dateStamp);
        var kRegion = HmacBytes(kDate, _region);
        var kService = HmacBytes(kRegion, Service);
        return HmacBytes(kService, "aws4_request");
    }

    private static string SortQueryString(string query)
    {
        if (string.IsNullOrEmpty(query)) return "";
        var pairs = query.Split('&')
            .Select(p => p.Split('=', 2))
            .OrderBy(p => p[0])
            .Select(p => p.Length == 2
                ? $"{Uri.EscapeDataString(Uri.UnescapeDataString(p[0]))}={Uri.EscapeDataString(Uri.UnescapeDataString(p[1]))}"
                : Uri.EscapeDataString(Uri.UnescapeDataString(p[0])));
        return string.Join("&", pairs);
    }

    private static string HexHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] HmacBytes(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string HexHmac(byte[] key, string data)
        => Convert.ToHexString(HmacBytes(key, data)).ToLowerInvariant();

    // --- HTTP helpers ---

    private static async Task<string> EnsureSuccessAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            // Try to extract Route 53 structured error message
            try
            {
                var doc = XDocument.Parse(body);
                var msg = doc.Descendants("Message").FirstOrDefault()?.Value
                       ?? doc.Descendants("message").FirstOrDefault()?.Value;
                var code = doc.Descendants("Code").FirstOrDefault()?.Value
                        ?? doc.Descendants("code").FirstOrDefault()?.Value;
                if (msg is not null)
                    throw new HttpRequestException(
                        $"Route 53 API error ({(int)resp.StatusCode}) {code}: {msg}",
                        inner: null,
                        statusCode: resp.StatusCode);
            }
            catch (System.Xml.XmlException) { }

            throw new HttpRequestException(
                $"Route 53 API returned {(int)resp.StatusCode}: {body}",
                inner: null,
                statusCode: resp.StatusCode);
        }

        return body;
    }

    /// <summary>
    /// Strip surrounding quotes from a Route 53 TXT record value.
    /// Route 53 returns TXT values as: "chunk1" "chunk2" for long values.
    /// </summary>
    private static string StripTxtQuotes(string value)
    {
        var result = new StringBuilder();
        var i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && value[i] == ' ') i++;
            if (i >= value.Length) break;
            if (value[i] == '"')
            {
                i++;
                while (i < value.Length && value[i] != '"')
                {
                    if (value[i] == '\\' && i + 1 < value.Length) { result.Append(value[i + 1]); i += 2; }
                    else { result.Append(value[i]); i++; }
                }
                if (i < value.Length) i++;
            }
            else
            {
                var start = i;
                while (i < value.Length && value[i] != ' ') i++;
                result.Append(value[start..i]);
            }
        }
        return result.ToString();
    }

    private static string EscapeTxt(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Route 53 (RFC 1035) limits each TXT string to 255 bytes.
    /// Long values (e.g. DKIM keys) must be split into 255-byte quoted chunks: "chunk1" "chunk2".
    /// </summary>
    private static string ChunkTxt(string value)
    {
        var escaped = EscapeTxt(value);
        if (Encoding.UTF8.GetByteCount(escaped) <= 255)
            return $"\"{escaped}\"";

        var chunks = new List<string>();
        var i = 0;
        while (i < escaped.Length)
        {
            // Walk forward until we hit 255 UTF-8 bytes
            var start = i;
            var byteCount = 0;
            while (i < escaped.Length)
            {
                var charBytes = Encoding.UTF8.GetByteCount(escaped[i].ToString());
                if (byteCount + charBytes > 255) break;
                byteCount += charBytes;
                i++;
            }
            chunks.Add($"\"{escaped[start..i]}\"");
        }
        return string.Join(" ", chunks);
    }
}
