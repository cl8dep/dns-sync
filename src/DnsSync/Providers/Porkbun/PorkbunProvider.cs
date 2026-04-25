using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Infrastructure;
using Microsoft.Extensions.Logging;
using static DnsSync.Core.DnsNameHelper;

namespace DnsSync.Providers.Porkbun;

/// <summary>
/// Porkbun DNS provider using the Porkbun API v3.
/// Authenticates with an API key + secret key pair.
/// Records are returned flat (one per value) and merged into RRsets for comparison.
/// </summary>
public class PorkbunProvider : IProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<PorkbunProvider> _logger;
    private readonly string _apiKey;
    private readonly string _secretKey;
    private const string BaseUrl = "https://api.porkbun.com/api/json/v3";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PorkbunProvider(string apiKey, string secretKey, ILogger<PorkbunProvider> logger)
    {
        _apiKey = apiKey;
        _secretKey = secretKey;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ─── IProvider ────────────────────────────────────────────────────────────

    public async Task PreflightAsync(CancellationToken ct = default)
    {
        var body = AuthBody();
        var json = await PostAsync("/ping", body, ct);
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("status", out var status)
            || !string.Equals(status.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Porkbun API authentication failed. Check your api_key and secret_key.");

        _logger.LogInformation("Porkbun API authenticated successfully");
    }

    public async Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default)
    {
        var domain = NormalizeZoneName(zoneName).TrimEnd('.');
        var json = await PostAsync($"/dns/retrieve/{domain}", AuthBody(), ct);
        var doc = JsonDocument.Parse(json);

        EnsureSuccess(doc, $"retrieve records for {domain}");

        var flat = new List<DnsRecord>();
        if (doc.RootElement.TryGetProperty("records", out var records))
        {
            foreach (var r in records.EnumerateArray())
            {
                var record = ParsePorkbunRecord(r, domain + ".");
                if (record is not null) flat.Add(record);
            }
        }

        return new DnsZone { Name = NormalizeZoneName(zoneName), Records = MergeIntoRRsets(flat) };
    }

    public async Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default)
    {
        var json = await PostAsync("/domain/listAll", AuthBody(), ct);
        var doc = JsonDocument.Parse(json);
        EnsureSuccess(doc, "list domains");

        var zones = new List<string>();
        if (doc.RootElement.TryGetProperty("domains", out var domains))
        {
            foreach (var d in domains.EnumerateArray())
            {
                if (d.TryGetProperty("domain", out var name) && name.GetString() is { } s)
                    zones.Add(NormalizeZoneName(s));
            }
        }
        return zones;
    }

    public async Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default)
    {
        if (plan.IsEmpty) return new ApplyResult(0, 0, []);

        var domain = NormalizeZoneName(zoneName).TrimEnd('.');

        // Build a map of (name, type) → list of Porkbun record IDs for updates/deletes
        var existingIds = await GetExistingRecordIds(domain, ct);

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
                        await CreateRecordsAsync(domain, change.After!, ct);
                        break;

                    case ChangeType.Update:
                        var key = (change.RecordName, change.RecordType);
                        if (existingIds.TryGetValue(key, out var ids))
                            await UpdateRecordsAsync(domain, ids, change.After!, ct);
                        else
                            await CreateRecordsAsync(domain, change.After!, ct);
                        break;

                    case ChangeType.Delete:
                        var delKey = (change.RecordName, change.RecordType);
                        if (existingIds.TryGetValue(delKey, out var delIds))
                            foreach (var id in delIds)
                                await DeleteRecordAsync(domain, id, ct);
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

    private async Task CreateRecordsAsync(string domain, DnsRecord record, CancellationToken ct)
    {
        _logger.LogDebug("Creating {Type} record {Name}", record.Type, record.Name);
        foreach (var payload in ToPayloads(record, domain))
        {
            var body = MergeAuth(payload);
            await PostAsync($"/dns/create/{domain}", body, ct);
        }
    }

    private async Task UpdateRecordsAsync(
        string domain, List<string> ids, DnsRecord record, CancellationToken ct)
    {
        _logger.LogDebug("Updating {Type} record {Name} ({Count} existing IDs)",
            record.Type, record.Name, ids.Count);

        var payloads = ToPayloads(record, domain);

        // PATCH the first existing ID
        if (payloads.Count > 0 && ids.Count > 0)
        {
            var body = MergeAuth(payloads[0]);
            await PostAsync($"/dns/edit/{domain}/{ids[0]}", body, ct);
        }

        // Delete extra existing IDs
        for (var i = 1; i < ids.Count; i++)
            await DeleteRecordAsync(domain, ids[i], ct);

        // Create additional values beyond the first
        for (var i = 1; i < payloads.Count; i++)
        {
            var body = MergeAuth(payloads[i]);
            await PostAsync($"/dns/create/{domain}", body, ct);
        }
    }

    private async Task DeleteRecordAsync(string domain, string id, CancellationToken ct)
    {
        _logger.LogDebug("Deleting record {RecordId} from domain {Domain}", id, domain);
        await PostAsync($"/dns/delete/{domain}/{id}", AuthBody(), ct);
    }

    private async Task<Dictionary<(string Name, string Type), List<string>>> GetExistingRecordIds(
        string domain, CancellationToken ct)
    {
        var json = await PostAsync($"/dns/retrieve/{domain}", AuthBody(), ct);
        var doc = JsonDocument.Parse(json);
        EnsureSuccess(doc, $"retrieve record IDs for {domain}");

        var result = new Dictionary<(string, string), List<string>>();
        if (!doc.RootElement.TryGetProperty("records", out var records)) return result;

        foreach (var r in records.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString()!;
            var name = NormalizeFqdn(r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
            var type = (r.TryGetProperty("type", out var t) ? t.GetString() : null)?.ToUpperInvariant() ?? "";

            var key = (name, type);
            if (!result.TryGetValue(key, out var list))
                result[key] = list = new List<string>();
            list.Add(id);
        }

        return result;
    }

    // ─── Record parsing ───────────────────────────────────────────────────────

    internal static DnsRecord? ParsePorkbunRecord(JsonElement r, string zoneName)
    {
        var type = (r.TryGetProperty("type", out var t) ? t.GetString() : null)?.ToUpperInvariant();
        if (type is null) return null;

        var rawName = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        // Porkbun returns name as subdomain only (e.g. "www") — build FQDN
        var name = BuildFqdn(rawName, zoneName);
        var ttl = r.TryGetProperty("ttl", out var ttlEl) && int.TryParse(ttlEl.GetString(), out var ttlVal)
            ? ttlVal : 600;
        var content = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var prio = r.TryGetProperty("prio", out var p) && int.TryParse(p.GetString(), out var prioVal)
            ? prioVal : 0;

        return type switch
        {
            "A" => new ARecord { Name = name, Type = "A", Ttl = ttl, Addresses = [content] },

            "AAAA" => new AaaaRecord { Name = name, Type = "AAAA", Ttl = ttl, Addresses = [content] },

            "CNAME" or "ALIAS" => new CnameRecord
            {
                Name = name,
                Type = "CNAME",
                Ttl = ttl,
                Target = NormalizeFqdn(content)
            },

            "MX" => new MxRecord
            {
                Name = name,
                Type = "MX",
                Ttl = ttl,
                Values = [new MxValue(prio, NormalizeFqdn(content))]
            },

            "TXT" => new TxtRecord
            {
                Name = name,
                Type = "TXT",
                Ttl = ttl,
                Values = [TxtRecord.ParseTxtContent(content)]
            },

            "NS" => new NsRecord
            {
                Name = name,
                Type = "NS",
                Ttl = ttl,
                Nameservers = [NormalizeFqdn(content)]
            },

            "CAA" => ParseCaaRecord(name, ttl, content),

            "SRV" => ParseSrvRecord(name, ttl, content, prio),

            _ => null
        };
    }

    private static DnsRecord? ParseCaaRecord(string name, int ttl, string content)
    {
        // Porkbun format: "flags tag \"value\""
        var parts = content.Split(' ', 3);
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

    private static DnsRecord? ParseSrvRecord(string name, int ttl, string content, int prio)
    {
        // Porkbun SRV content: "weight port target"
        var parts = content.Split(' ', 3);
        return new SrvRecord
        {
            Name = name,
            Type = "SRV",
            Ttl = ttl,
            Values = [new SrvValue(
                prio,
                int.TryParse(parts.ElementAtOrDefault(0), out var w) ? w : 0,
                int.TryParse(parts.ElementAtOrDefault(1), out var port) ? port : 0,
                NormalizeFqdn(parts.ElementAtOrDefault(2) ?? ""))]
        };
    }

    // ─── Payload builders ─────────────────────────────────────────────────────

    private static List<Dictionary<string, object>> ToPayloads(DnsRecord record, string domain)
    {
        var subdomain = SubdomainOf(record.Name, domain + ".");
        var ttl = record.Ttl;

        return record switch
        {
            ARecord a => a.Addresses.Select(addr => new Dictionary<string, object>
            { ["type"] = "A", ["name"] = subdomain, ["content"] = addr, ["ttl"] = ttl }).ToList(),

            AaaaRecord aaaa => aaaa.Addresses.Select(addr => new Dictionary<string, object>
            { ["type"] = "AAAA", ["name"] = subdomain, ["content"] = addr, ["ttl"] = ttl }).ToList(),

            CnameRecord cname => [new Dictionary<string, object>
                { ["type"] = "CNAME", ["name"] = subdomain, ["content"] = cname.Target.TrimEnd('.'), ["ttl"] = ttl }],

            MxRecord mx => mx.Values.Select(v => new Dictionary<string, object>
            {
                ["type"] = "MX",
                ["name"] = subdomain,
                ["content"] = v.Exchange.TrimEnd('.'),
                ["prio"] = v.Preference,
                ["ttl"] = ttl
            }).ToList(),

            TxtRecord txt => txt.Values.Select(v => new Dictionary<string, object>
            { ["type"] = "TXT", ["name"] = subdomain, ["content"] = v, ["ttl"] = ttl }).ToList(),

            NsRecord ns => ns.Nameservers.Select(n => new Dictionary<string, object>
            { ["type"] = "NS", ["name"] = subdomain, ["content"] = n.TrimEnd('.'), ["ttl"] = ttl }).ToList(),

            CaaRecord caa => caa.Values.Select(v => new Dictionary<string, object>
            {
                ["type"] = "CAA",
                ["name"] = subdomain,
                ["content"] = $"{v.Flags} {v.Tag} \"{v.Value}\"",
                ["ttl"] = ttl
            }).ToList(),

            SrvRecord srv => srv.Values.Select(v => new Dictionary<string, object>
            {
                ["type"] = "SRV",
                ["name"] = subdomain,
                ["content"] = $"{v.Weight} {v.Port} {v.Target.TrimEnd('.')}",
                ["prio"] = v.Priority,
                ["ttl"] = ttl
            }).ToList(),

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

    private async Task<string> PostAsync(string path, Dictionary<string, object> body, CancellationToken ct) =>
        await HttpRetryPolicy.ExecuteAsync(async () =>
        {
            _logger.LogDebug("POST {Url}", BaseUrl + path);
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(BaseUrl + path, content, ct);
            var responseBody = await EnsureSuccessAsync(resp);
            _logger.LogDebug("← {StatusCode} ({Bytes} bytes)", (int)resp.StatusCode, responseBody.Length);
            return responseBody;
        }, _logger, ct);

    private static async Task<string> EnsureSuccessAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Porkbun API rate limit exceeded.", null, resp.StatusCode);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Porkbun API returned {(int)resp.StatusCode}: {body}",
                null, resp.StatusCode);

        return body;
    }

    private static void EnsureSuccess(JsonDocument doc, string context)
    {
        if (doc.RootElement.TryGetProperty("status", out var status)
            && string.Equals(status.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            return;

        var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        throw new InvalidOperationException(
            $"Porkbun API error ({context}): {message ?? "unknown error"}");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private Dictionary<string, object> AuthBody() => new()
    {
        ["apikey"] = _apiKey,
        ["secretapikey"] = _secretKey
    };

    private Dictionary<string, object> MergeAuth(Dictionary<string, object> payload)
    {
        var merged = new Dictionary<string, object>(payload)
        {
            ["apikey"] = _apiKey,
            ["secretapikey"] = _secretKey
        };
        return merged;
    }

    private static string BuildFqdn(string name, string zoneName)
    {
        var zone = zoneName.TrimEnd('.');
        if (string.IsNullOrEmpty(name) || string.Equals(name, zone, StringComparison.OrdinalIgnoreCase))
            return zoneName;
        if (name.EndsWith('.'))
            return name.ToLowerInvariant();
        // Porkbun returns full domain names (e.g. "www.example.com"), not just subdomains
        if (name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase) || string.Equals(name, zone, StringComparison.OrdinalIgnoreCase))
            return name.ToLowerInvariant() + ".";
        return $"{name.ToLowerInvariant()}.{zoneName}";
    }

    private static string SubdomainOf(string fqdn, string zoneName)
    {
        var zone = zoneName.TrimEnd('.');
        var name = fqdn.TrimEnd('.');
        if (string.Equals(name, zone, StringComparison.OrdinalIgnoreCase))
            return "";
        if (name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            return name[..^(zone.Length + 1)];
        return name;
    }
}
