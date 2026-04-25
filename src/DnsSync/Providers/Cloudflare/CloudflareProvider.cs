using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DnsSync.Providers.Cloudflare;

/// <summary>
/// Cloudflare DNS provider using the Cloudflare API v4.
/// Authenticates with a scoped API token (Zone:DNS:Edit).
/// Rate limit: 1200 requests/5 minutes — retries on 429 and transient errors with exponential backoff.
/// </summary>
public class CloudflareProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<CloudflareProvider> _logger;
    private readonly string? _accountId;
    private const string BaseUrl = "https://api.cloudflare.com/client/v4";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CloudflareProvider(string apiToken, ILogger<CloudflareProvider> logger, string? accountId = null)
    {
        _logger = logger;
        _accountId = accountId;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiToken);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task PreflightAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync("/user/tokens/verify", ct);
        var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
            throw new InvalidOperationException(
                "Cloudflare API token verification failed. " +
                "Ensure the token has Zone:DNS:Edit permissions.");
        _logger.LogInformation("Cloudflare token verified successfully");

        if (_accountId is not null)
            await VerifyAccountAsync(ct);
    }

    private async Task VerifyAccountAsync(CancellationToken ct)
    {
        try
        {
            var resp = await GetAsync($"/accounts/{Uri.EscapeDataString(_accountId!)}", ct);
            var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                _logger.LogWarning(
                    "Could not verify Cloudflare account '{AccountId}' — " +
                    "the token may lack 'Account Settings: Read'. Zone filtering by account_id is still active.",
                    _accountId);
                return;
            }

            var accountName = doc.RootElement
                .TryGetProperty("result", out var result) && result.TryGetProperty("name", out var name)
                ? name.GetString()
                : _accountId;

            _logger.LogInformation("Cloudflare account verified: {AccountName} ({AccountId})", accountName, _accountId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not verify Cloudflare account '{AccountId}': {Error}. " +
                "Zone filtering by account_id is still active.",
                _accountId, ex.Message);
        }
    }

    public async Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default)
    {
        var normalized = DnsNameHelper.NormalizeZoneName(zoneName);
        var zoneId = await ResolveZoneIdAsync(normalized, ct);
        var records = await GetAllRecordsAsync(zoneId, normalized, ct);
        return new DnsZone { Name = normalized, Records = records };
    }

    public async Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default)
    {
        var normalized = DnsNameHelper.NormalizeZoneName(zoneName);
        var zoneId = await ResolveZoneIdAsync(normalized, ct);
        var existing = await GetExistingRecordMap(zoneId, ct);

        var applied = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var change in plan.Changes)
        {
            try
            {
                switch (change.ChangeType)
                {
                    case ChangeType.Create:
                        await CreateRecordAsync(zoneId, change.After!, ct);
                        break;

                    case ChangeType.Update:
                        var key = (change.RecordName, change.RecordType);
                        if (existing.TryGetValue(key, out var existingIds))
                            await UpdateRecordAsync(zoneId, existingIds, change.After!, ct);
                        else
                            await CreateRecordAsync(zoneId, change.After!, ct);
                        break;

                    case ChangeType.Delete:
                        var delKey = (change.RecordName, change.RecordType);
                        if (existing.TryGetValue(delKey, out var deleteIds))
                            foreach (var id in deleteIds)
                                await DeleteRecordAsync(zoneId, id, ct);
                        break;
                }
                applied++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{change.ChangeType} {change.RecordName} {change.RecordType}: {ex.Message}");
                _logger.LogWarning("Failed to apply change {ChangeType} {Name} {Type}: {Error}",
                    change.ChangeType, change.RecordName, change.RecordType, ex.Message);
            }
        }

        return new ApplyResult(applied, failed, errors);
    }

    public async Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default)
    {
        var zones = new List<string>();
        var page = 1;
        var accountFilter = _accountId is not null
            ? $"&account.id={Uri.EscapeDataString(_accountId)}"
            : string.Empty;

        while (true)
        {
            var json = await GetAsync($"/zones?per_page=200&page={page}{accountFilter}", ct);
            var doc = JsonDocument.Parse(json);

            foreach (var z in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                var name = DnsNameHelper.NormalizeZoneName(z.GetProperty("name").GetString() ?? "");
                if (name.Length > 1) zones.Add(name);
            }

            int totalPages = 1;
            if (doc.RootElement.TryGetProperty("result_info", out var info)
                && info.TryGetProperty("total_pages", out var tp))
                totalPages = tp.GetInt32();

            if (page >= totalPages) break;
            page++;
        }

        return zones;
    }

    // --- Private helpers ---

    private async Task<string> ResolveZoneIdAsync(string zoneName, CancellationToken ct)
    {
        var name = zoneName.TrimEnd('.');
        var accountFilter = _accountId is not null
            ? $"&account.id={Uri.EscapeDataString(_accountId)}"
            : string.Empty;
        var json = await GetAsync($"/zones?name={Uri.EscapeDataString(name)}{accountFilter}", ct);
        var doc = JsonDocument.Parse(json);

        var result = doc.RootElement.GetProperty("result");
        if (result.GetArrayLength() == 0)
            throw new InvalidOperationException(
                $"Zone '{zoneName}' not found in Cloudflare account. " +
                "Ensure the zone is added to your account and the token has access to it.");

        return result[0].GetProperty("id").GetString()!;
    }

    private async Task<IReadOnlyList<DnsRecord>> GetAllRecordsAsync(
        string zoneId, string zoneName, CancellationToken ct)
    {
        var flat = new List<DnsRecord>();
        var page = 1;

        while (true)
        {
            var json = await GetAsync(
                $"/zones/{zoneId}/dns_records?per_page=200&page={page}", ct);
            var doc = JsonDocument.Parse(json);

            var result = doc.RootElement.GetProperty("result");
            foreach (var r in result.EnumerateArray())
            {
                var record = ParseCloudflareRecord(r, zoneName);
                if (record is not null)
                    flat.Add(record);
            }

            int totalPages = 1;
            if (doc.RootElement.TryGetProperty("result_info", out var info)
                && info.TryGetProperty("total_pages", out var tp))
                totalPages = tp.GetInt32();

            _logger.LogDebug("Fetched DNS records page {Page}/{Total} for zone {ZoneId}", page, totalPages, zoneId);

            if (page >= totalPages) break;
            page++;
        }

        // Cloudflare returns one entry per value (e.g. one entry per IP for A records).
        // Merge into RRset-level records so ZoneDiff can compare them correctly against
        // YAML sources that store all values in a single record.
        return MergeIntoRRsets(flat);
    }

    /// <summary>
    /// Merges flat Cloudflare records (one per value) into DNS RRset-level records
    /// (one per name+type, with all values combined). This normalizes the Cloudflare
    /// representation to match how YamlProvider returns records.
    /// </summary>
    private static IReadOnlyList<DnsRecord> MergeIntoRRsets(List<DnsRecord> flat)
    {
        var merged = new List<DnsRecord>();

        foreach (var group in flat.GroupBy(r => (r.Name, r.Type)))
        {
            var records = group.ToList();
            if (records.Count == 1)
            {
                merged.Add(records[0]);
                continue;
            }

            var first = records[0];
            DnsRecord? rrset = first switch
            {
                ARecord => new ARecord
                {
                    Name = first.Name,
                    Type = first.Type,
                    Ttl = first.Ttl,
                    Addresses = records.Cast<ARecord>().SelectMany(r => r.Addresses).ToList()
                },
                AaaaRecord => new AaaaRecord
                {
                    Name = first.Name,
                    Type = first.Type,
                    Ttl = first.Ttl,
                    Addresses = records.Cast<AaaaRecord>().SelectMany(r => r.Addresses).ToList()
                },
                MxRecord => new MxRecord
                {
                    Name = first.Name,
                    Type = first.Type,
                    Ttl = first.Ttl,
                    Values = records.Cast<MxRecord>().SelectMany(r => r.Values).ToList()
                },
                TxtRecord => new TxtRecord
                {
                    Name = first.Name,
                    Type = first.Type,
                    Ttl = first.Ttl,
                    Values = records.Cast<TxtRecord>().SelectMany(r => r.Values).ToList()
                },
                NsRecord => new NsRecord
                {
                    Name = first.Name,
                    Type = first.Type,
                    Ttl = first.Ttl,
                    Nameservers = records.Cast<NsRecord>().SelectMany(r => r.Nameservers).ToList()
                },
                CaaRecord => new CaaRecord
                {
                    Name = first.Name,
                    Type = first.Type,
                    Ttl = first.Ttl,
                    Values = records.Cast<CaaRecord>().SelectMany(r => r.Values).ToList()
                },
                SrvRecord => new SrvRecord
                {
                    Name = first.Name,
                    Type = first.Type,
                    Ttl = first.Ttl,
                    Values = records.Cast<SrvRecord>().SelectMany(r => r.Values).ToList()
                },
                _ => first
            };
            merged.Add(rrset);
        }

        return merged;
    }

    /// <summary>
    /// Returns a map of (name, type) → list of Cloudflare record IDs.
    /// Multi-value records (e.g. A with multiple IPs) have multiple IDs per key.
    /// </summary>
    private async Task<Dictionary<(string Name, string Type), List<string>>> GetExistingRecordMap(
        string zoneId, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), List<string>>();
        var page = 1;

        while (true)
        {
            var json = await GetAsync($"/zones/{zoneId}/dns_records?per_page=200&page={page}", ct);
            var doc = JsonDocument.Parse(json);

            foreach (var r in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                if (!r.TryGetProperty("id", out var idEl) || !r.TryGetProperty("name", out var nameEl))
                    continue;

                var key = (DnsNameHelper.NormalizeFqdn(nameEl.GetString()!),
                           r.GetProperty("type").GetString()!.ToUpperInvariant());
                var id = idEl.GetString()!;

                if (!result.TryGetValue(key, out var ids))
                    result[key] = ids = new List<string>();
                ids.Add(id);
            }

            int totalPages = 1;
            if (doc.RootElement.TryGetProperty("result_info", out var info)
                && info.TryGetProperty("total_pages", out var tp))
                totalPages = tp.GetInt32();

            if (page >= totalPages) break;
            page++;
        }

        return result;
    }

    private DnsRecord? ParseCloudflareRecord(JsonElement r, string zoneName)
    {
        var type = r.GetProperty("type").GetString()?.ToUpperInvariant() ?? "";
        var name = DnsNameHelper.NormalizeFqdn(r.GetProperty("name").GetString() ?? "");
        var ttl = r.TryGetProperty("ttl", out var ttlEl) ? ttlEl.GetInt32() : 3600;
        // Cloudflare uses TTL=1 to mean "automatic" (proxied records). Treat as 300.
        if (ttl == 1) ttl = 300;

        return type switch
        {
            "A" => new ARecord
            {
                Name = name,
                Type = "A",
                Ttl = ttl,
                Addresses = [r.GetProperty("content").GetString()!]
            },
            "AAAA" => new AaaaRecord
            {
                Name = name,
                Type = "AAAA",
                Ttl = ttl,
                Addresses = [r.GetProperty("content").GetString()!]
            },
            "CNAME" => new CnameRecord
            {
                Name = name,
                Type = "CNAME",
                Ttl = ttl,
                Target = DnsNameHelper.NormalizeFqdn(r.GetProperty("content").GetString()!)
            },
            "MX" => new MxRecord
            {
                Name = name,
                Type = "MX",
                Ttl = ttl,
                Values =
                [
                    new MxValue(
                        r.TryGetProperty("priority", out var prio) ? prio.GetInt32() : 10,
                        DnsNameHelper.NormalizeFqdn(r.GetProperty("content").GetString()!))
                ]
            },
            "TXT" => new TxtRecord
            {
                Name = name,
                Type = "TXT",
                Ttl = ttl,
                Values = [TxtRecord.ParseTxtContent(r.GetProperty("content").GetString()!)]
            },
            "NS" => new NsRecord
            {
                Name = name,
                Type = "NS",
                Ttl = ttl,
                Nameservers = [DnsNameHelper.NormalizeFqdn(r.GetProperty("content").GetString()!)]
            },
            "SRV" => ParseSrvFromCloudflare(name, ttl, r),
            "CAA" => ParseCaaFromCloudflare(name, ttl, r),
            _ => null
        };
    }

    private static DnsRecord? ParseCaaFromCloudflare(string name, int ttl, JsonElement r)
    {
        var content = r.GetProperty("content").GetString() ?? "";
        var parts = content.Split(' ', 3);
        if (parts.Length < 3) return null;

        return new CaaRecord
        {
            Name = name,
            Type = "CAA",
            Ttl = ttl,
            Values =
            [
                new CaaValue(
                    int.TryParse(parts[0], out var f) ? f : 0,
                    parts[1],
                    parts[2].Trim('"'))
            ]
        };
    }

    private static DnsRecord? ParseSrvFromCloudflare(string name, int ttl, JsonElement r)
    {
        // Cloudflare SRV is in a structured 'data' object
        if (!r.TryGetProperty("data", out var data)) return null;

        return new SrvRecord
        {
            Name = name,
            Type = "SRV",
            Ttl = ttl,
            Values =
            [
                new SrvValue(
                    data.TryGetProperty("priority", out var prio) ? prio.GetInt32() : 0,
                    data.TryGetProperty("weight", out var wt) ? wt.GetInt32() : 0,
                    data.TryGetProperty("port", out var port) ? port.GetInt32() : 0,
                    DnsNameHelper.NormalizeFqdn(
                        data.TryGetProperty("target", out var tgt) ? tgt.GetString() ?? "" : ""))
            ]
        };
    }

    private async Task CreateRecordAsync(string zoneId, DnsRecord record, CancellationToken ct)
    {
        _logger.LogDebug("Creating {Type} record {Name}", record.Type, record.Name);
        foreach (var body in ToCloudflarePayloads(record))
        {
            var json = JsonSerializer.Serialize(body, JsonOpts);
            await PostAsync($"/zones/{zoneId}/dns_records", json, ct);
        }
    }

    /// <summary>
    /// Update a multi-value record: patch the first existing ID, delete extras, POST any additional payloads.
    /// </summary>
    private async Task UpdateRecordAsync(
        string zoneId, List<string> existingIds, DnsRecord record, CancellationToken ct)
    {
        _logger.LogDebug("Updating {Type} record {Name} ({Count} existing IDs)", record.Type, record.Name, existingIds.Count);

        var payloads = ToCloudflarePayloads(record);
        if (payloads.Count == 0) return;

        // PATCH the first existing record
        var firstJson = JsonSerializer.Serialize(payloads[0], JsonOpts);
        await PatchAsync($"/zones/{zoneId}/dns_records/{existingIds[0]}", firstJson, ct);

        // Delete any extra existing records beyond the first
        for (var i = 1; i < existingIds.Count; i++)
            await DeleteRecordAsync(zoneId, existingIds[i], ct);

        // POST any additional payloads (new IPs / exchanges)
        for (var i = 1; i < payloads.Count; i++)
        {
            var json = JsonSerializer.Serialize(payloads[i], JsonOpts);
            await PostAsync($"/zones/{zoneId}/dns_records", json, ct);
        }
    }

    private async Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken ct)
    {
        _logger.LogDebug("Deleting record {RecordId} from zone {ZoneId}", recordId, zoneId);
        await DeleteAsync($"/zones/{zoneId}/dns_records/{recordId}", ct);
    }

    private static List<Dictionary<string, object>> ToCloudflarePayloads(DnsRecord record)
    {
        var name = record.Name.TrimEnd('.');
        var ttl = record.Ttl;

        return record switch
        {
            ARecord a => a.Addresses.Select(addr => new Dictionary<string, object>
            { ["type"] = "A", ["name"] = name, ["content"] = addr, ["ttl"] = ttl }).ToList(),

            AaaaRecord aaaa => aaaa.Addresses.Select(addr => new Dictionary<string, object>
            { ["type"] = "AAAA", ["name"] = name, ["content"] = addr, ["ttl"] = ttl }).ToList(),

            CnameRecord cname => [new Dictionary<string, object>
                { ["type"] = "CNAME", ["name"] = name, ["content"] = cname.Target.TrimEnd('.'), ["ttl"] = ttl }],

            MxRecord mx => mx.Values.Select(v => new Dictionary<string, object>
            {
                ["type"] = "MX",
                ["name"] = name,
                ["content"] = v.Exchange.TrimEnd('.'),
                ["priority"] = v.Preference,
                ["ttl"] = ttl
            }).ToList(),

            TxtRecord txt => txt.Values.Select(v => new Dictionary<string, object>
            { ["type"] = "TXT", ["name"] = name, ["content"] = v, ["ttl"] = ttl }).ToList(),

            NsRecord ns => ns.Nameservers.Select(n => new Dictionary<string, object>
            { ["type"] = "NS", ["name"] = name, ["content"] = n.TrimEnd('.'), ["ttl"] = ttl }).ToList(),

            CaaRecord caa => caa.Values.Select(v => new Dictionary<string, object>
            { ["type"] = "CAA", ["name"] = name, ["content"] = $"{v.Flags} {v.Tag} \"{v.Value}\"", ["ttl"] = ttl }).ToList(),

            SrvRecord srv => srv.Values.Select(v => new Dictionary<string, object>
            {
                ["type"] = "SRV",
                ["name"] = name,
                ["ttl"] = ttl,
                ["data"] = new Dictionary<string, object>
                {
                    ["priority"] = v.Priority,
                    ["weight"] = v.Weight,
                    ["port"] = v.Port,
                    ["target"] = v.Target.TrimEnd('.')
                }
            }).ToList(),

            _ => []
        };
    }

    // --- HTTP helpers with retry ---

    private async Task<string> GetAsync(string path, CancellationToken ct) =>
        await HttpRetryPolicy.ExecuteAsync(
            async () => await EnsureSuccessAsync(await _http.GetAsync(BaseUrl + path, ct)),
            _logger, ct);

    private async Task<string> PostAsync(string path, string json, CancellationToken ct) =>
        await HttpRetryPolicy.ExecuteAsync(async () =>
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await EnsureSuccessAsync(await _http.PostAsync(BaseUrl + path, content, ct));
        }, _logger, ct);

    private async Task<string> PatchAsync(string path, string json, CancellationToken ct) =>
        await HttpRetryPolicy.ExecuteAsync(async () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, BaseUrl + path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return await EnsureSuccessAsync(await _http.SendAsync(request, ct));
        }, _logger, ct);

    private async Task<string> DeleteAsync(string path, CancellationToken ct) =>
        await HttpRetryPolicy.ExecuteAsync(
            async () => await EnsureSuccessAsync(await _http.DeleteAsync(BaseUrl + path, ct)),
            _logger, ct);

    private static async Task<string> EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new CloudflareRateLimitException();

        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            // Try to extract Cloudflare structured error messages
            if (body.Length > 0)
            {
                try
                {
                    var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("errors", out var errsEl))
                    {
                        var msgs = errsEl.EnumerateArray()
                            .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                            .Where(m => m is not null)
                            .ToList();

                        if (msgs.Count > 0)
                            throw new HttpRequestException(
                                $"Cloudflare API error ({(int)resp.StatusCode}): {string.Join("; ", msgs)}",
                                inner: null,
                                statusCode: resp.StatusCode);
                    }
                }
                catch (JsonException) { /* fall through to generic error */ }
            }

            throw new HttpRequestException(
                $"Cloudflare API returned {(int)resp.StatusCode}: {body}",
                inner: null,
                statusCode: resp.StatusCode);
        }

        return body;
    }

}

internal sealed class CloudflareRateLimitException : Exception { }
