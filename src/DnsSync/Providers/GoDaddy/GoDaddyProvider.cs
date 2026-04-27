using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Infrastructure;
using Microsoft.Extensions.Logging;
using static DnsSync.Core.DnsNameHelper;

namespace DnsSync.Providers.GoDaddy;

/// <summary>
/// GoDaddy DNS provider using the GoDaddy Domains API v1.
/// Authenticates with an API key + secret pair (sso-key scheme).
/// Uses PUT /v1/domains/{domain}/records/{type}/{name} to replace full RRsets atomically.
/// </summary>
public class GoDaddyProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<GoDaddyProvider> _logger;
    private const string BaseUrl = "https://api.godaddy.com";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GoDaddyProvider(string apiKey, string secretKey, ILogger<GoDaddyProvider> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("sso-key", $"{apiKey}:{secretKey}");
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ─── IProvider ────────────────────────────────────────────────────────────

    public async Task PreflightAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"{BaseUrl}/v1/domains?limit=1", ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                "GoDaddy API authentication failed. Check your api_key and secret_key.");

        await EnsureSuccessAsync(resp, "preflight");
        _logger.LogInformation("GoDaddy API authenticated successfully");
    }

    public async Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default)
    {
        var zones = new List<string>();
        var marker = (string?)null;

        do
        {
            var url = $"{BaseUrl}/v1/domains?limit=100&statuses=ACTIVE"
                      + (marker is not null ? $"&marker={marker}" : "");
            var resp = await _http.GetAsync(url, ct);
            await EnsureSuccessAsync(resp, "list domains");

            var body = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);

            marker = null;
            foreach (var domain in doc.RootElement.EnumerateArray())
            {
                if (domain.TryGetProperty("domain", out var d) && d.GetString() is { } name)
                    zones.Add(NormalizeZoneName(name));

                // pagination: last domain name becomes the next marker
                marker = domain.TryGetProperty("domain", out var last) ? last.GetString() : null;
            }

            // GoDaddy paginates by returning exactly `limit` items when there are more
            if (doc.RootElement.GetArrayLength() < 100)
                marker = null;

        } while (marker is not null);

        return zones;
    }

    public async Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default)
    {
        var domain = NormalizeZoneName(zoneName).TrimEnd('.');
        var resp = await _http.GetAsync($"{BaseUrl}/v1/domains/{domain}/records", ct);
        await EnsureSuccessAsync(resp, $"get records for {domain}");

        var body = await resp.Content.ReadAsStringAsync(ct);
        var flat = new List<DnsRecord>();

        foreach (var r in JsonDocument.Parse(body).RootElement.EnumerateArray())
        {
            var record = ParseRecord(r, domain + ".");
            if (record is not null) flat.Add(record);
        }

        return new DnsZone { Name = NormalizeZoneName(zoneName), Records = MergeIntoRRsets(flat) };
    }

    public async Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default)
    {
        if (plan.IsEmpty) return new ApplyResult(0, 0, []);

        var domain = NormalizeZoneName(zoneName).TrimEnd('.');
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
                    case ChangeType.Update:
                        await PutRRsetAsync(domain, change.After!, ct);
                        break;

                    case ChangeType.Delete:
                        await DeleteRRsetAsync(domain, change.RecordType, change.RecordName, ct);
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

    // ─── Record operations ────────────────────────────────────────────────────

    private async Task PutRRsetAsync(string domain, DnsRecord record, CancellationToken ct)
    {
        var name = RelativeName(record.Name, domain + ".");
        var type = record.Type;
        _logger.LogDebug("PUT {Type} {Name} on {Domain}", type, name, domain);

        var payloads = ToPayloads(record, domain);
        var json = JsonSerializer.Serialize(payloads, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await HttpRetryPolicy.ExecuteAsync(async () =>
        {
            var r = await _http.PutAsync($"{BaseUrl}/v1/domains/{domain}/records/{type}/{name}", content, ct);
            await EnsureSuccessAsync(r, $"PUT {type} {name}");
            return r;
        }, _logger, ct);
    }

    private async Task DeleteRRsetAsync(string domain, string type, string fqdn, CancellationToken ct)
    {
        var name = RelativeName(fqdn, domain + ".");
        _logger.LogDebug("DELETE {Type} {Name} on {Domain}", type, name, domain);

        await HttpRetryPolicy.ExecuteAsync(async () =>
        {
            var resp = await _http.DeleteAsync($"{BaseUrl}/v1/domains/{domain}/records/{type}/{name}", ct);
            // 404 is fine on delete — record was already gone
            if (resp.StatusCode != HttpStatusCode.NotFound)
                await EnsureSuccessAsync(resp, $"DELETE {type} {name}");
            return resp;
        }, _logger, ct);
    }

    // ─── Record parsing ───────────────────────────────────────────────────────

    internal static DnsRecord? ParseRecord(JsonElement r, string zoneName)
    {
        var type = (r.TryGetProperty("type", out var t) ? t.GetString() : null)?.ToUpperInvariant();
        if (type is null) return null;

        var rawName = r.TryGetProperty("name", out var n) ? n.GetString() ?? "@" : "@";
        var fqdn = BuildFqdn(rawName, zoneName);
        var ttl = r.TryGetProperty("ttl", out var ttlEl) ? ttlEl.GetInt32() : 600;
        var data = r.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
        var priority = r.TryGetProperty("priority", out var p) ? p.GetInt32() : 0;

        return type switch
        {
            "A" => new ARecord { Name = fqdn, Type = "A", Ttl = ttl, Addresses = [data] },

            "AAAA" => new AaaaRecord { Name = fqdn, Type = "AAAA", Ttl = ttl, Addresses = [data] },

            "CNAME" => new CnameRecord
            {
                Name = fqdn,
                Type = "CNAME",
                Ttl = ttl,
                Target = NormalizeFqdn(data)
            },

            "MX" => new MxRecord
            {
                Name = fqdn,
                Type = "MX",
                Ttl = ttl,
                Values = [new MxValue(priority, NormalizeFqdn(data))]
            },

            "TXT" => new TxtRecord
            {
                Name = fqdn,
                Type = "TXT",
                Ttl = ttl,
                Values = [TxtRecord.ParseTxtContent(data)]
            },

            "NS" => new NsRecord
            {
                Name = fqdn,
                Type = "NS",
                Ttl = ttl,
                Nameservers = [NormalizeFqdn(data)]
            },

            "CAA" => ParseCaaRecord(fqdn, ttl, data),

            "SRV" => ParseSrvRecord(fqdn, ttl, data, priority,
                r.TryGetProperty("weight", out var w) ? w.GetInt32() : 0,
                r.TryGetProperty("port", out var port) ? port.GetInt32() : 0,
                r.TryGetProperty("service", out var svc) ? svc.GetString() ?? "" : "",
                r.TryGetProperty("protocol", out var proto) ? proto.GetString() ?? "" : ""),

            _ => null
        };
    }

    private static DnsRecord? ParseCaaRecord(string name, int ttl, string data)
    {
        // GoDaddy CAA data format: "flags tag \"value\""
        var parts = data.Split(' ', 3);
        if (parts.Length < 3) return null;
        return new CaaRecord
        {
            Name = name,
            Type = "CAA",
            Ttl = ttl,
            Values = [new CaaValue(
                int.TryParse(parts[0], out var flags) ? flags : 0,
                parts[1],
                parts[2].Trim('"'))]
        };
    }

    private static DnsRecord? ParseSrvRecord(
        string name, int ttl, string data, int priority, int weight, int port,
        string service, string protocol)
    {
        // GoDaddy returns SRV target in data field; service/protocol are separate fields
        return new SrvRecord
        {
            Name = name,
            Type = "SRV",
            Ttl = ttl,
            Values = [new SrvValue(priority, weight, port, NormalizeFqdn(data))]
        };
    }

    // ─── Payload builders ─────────────────────────────────────────────────────

    private static List<GoDaddyRecord> ToPayloads(DnsRecord record, string domain)
    {
        var name = RelativeName(record.Name, domain + ".");
        var ttl = record.Ttl;

        return record switch
        {
            ARecord a => a.Addresses.Select(addr =>
                new GoDaddyRecord(name, "A", addr, ttl, 0)).ToList(),

            AaaaRecord aaaa => aaaa.Addresses.Select(addr =>
                new GoDaddyRecord(name, "AAAA", addr, ttl, 0)).ToList(),

            CnameRecord cname => [new GoDaddyRecord(name, "CNAME", cname.Target.TrimEnd('.'), ttl, 0)],

            MxRecord mx => mx.Values.Select(v =>
                new GoDaddyRecord(name, "MX", v.Exchange.TrimEnd('.'), ttl, v.Preference)).ToList(),

            TxtRecord txt => txt.Values.Select(v =>
                new GoDaddyRecord(name, "TXT", v, ttl, 0)).ToList(),

            NsRecord ns => ns.Nameservers.Select(n =>
                new GoDaddyRecord(name, "NS", n.TrimEnd('.'), ttl, 0)).ToList(),

            CaaRecord caa => caa.Values.Select(v =>
                new GoDaddyRecord(name, "CAA", $"{v.Flags} {v.Tag} \"{v.Value}\"", ttl, 0)).ToList(),

            SrvRecord srv => srv.Values.Select(v =>
                new GoDaddyRecord(name, "SRV", v.Target.TrimEnd('.'), ttl, v.Priority, v.Weight, v.Port)).ToList(),

            _ => []
        };
    }

    // ─── RRset merging ────────────────────────────────────────────────────────

    private static IReadOnlyList<DnsRecord> MergeIntoRRsets(List<DnsRecord> flat)
    {
        var merged = new List<DnsRecord>();
        foreach (var group in flat.GroupBy(r => (r.Name, r.Type)))
        {
            var records = group.ToList();
            if (records.Count == 1) { merged.Add(records[0]); continue; }

            var first = records[0];
            DnsRecord rrset = first switch
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

    // ─── HTTP helpers ─────────────────────────────────────────────────────────

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string context)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GoDaddy API rate limit exceeded (60 req/min).", null, resp.StatusCode);

        throw new HttpRequestException(
            $"GoDaddy API error ({context}) [{(int)resp.StatusCode}]: {body}",
            null, resp.StatusCode);
    }

    // ─── Name helpers ─────────────────────────────────────────────────────────

    private static string BuildFqdn(string name, string zoneName)
    {
        if (name == "@" || string.IsNullOrEmpty(name))
            return zoneName;
        if (name.EndsWith('.'))
            return name.ToLowerInvariant();
        return $"{name.ToLowerInvariant()}.{zoneName}";
    }

    private static string RelativeName(string fqdn, string zoneName)
    {
        var zone = zoneName.TrimEnd('.');
        var name = fqdn.TrimEnd('.');
        if (string.Equals(name, zone, StringComparison.OrdinalIgnoreCase))
            return "@";
        if (name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            return name[..^(zone.Length + 1)];
        return name;
    }
}

// ─── Payload DTOs ─────────────────────────────────────────────────────────────

internal class GoDaddyRecord
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("data")] public string Data { get; init; } = "";
    [JsonPropertyName("ttl")] public int Ttl { get; init; }
    [JsonPropertyName("priority")] public int Priority { get; init; }
    [JsonPropertyName("weight")] public int? Weight { get; init; }
    [JsonPropertyName("port")] public int? Port { get; init; }

    public GoDaddyRecord() { }

    public GoDaddyRecord(string name, string type, string data, int ttl, int priority)
    {
        Name = name; Type = type; Data = data; Ttl = ttl; Priority = priority;
    }

    public GoDaddyRecord(string name, string type, string data, int ttl, int priority, int weight, int port)
        : this(name, type, data, ttl, priority)
    {
        Weight = weight; Port = port;
    }
}
