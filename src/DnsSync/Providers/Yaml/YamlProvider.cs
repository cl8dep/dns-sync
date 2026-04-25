using DnsSync.Core;
using DnsSync.Core.Records;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static DnsSync.Core.DnsNameHelper;

namespace DnsSync.Providers.Yaml;

/// <summary>
/// Reads DNS zone data from YAML files on disk.
/// This provider is read-only (source only); ApplyPlanAsync throws NotSupportedException.
///
/// Zone file format (zones/{zone}.yaml):
///   subdomain:
///     type: A
///     ttl: 3600
///     values: [1.2.3.4]
///
///   # Multiple records at the same name (list form):
///   '':
///     - type: A
///       ttl: 3600
///       values: [1.2.3.4, 5.6.7.8]
///     - type: MX
///       ttl: 600
///       values:
///         - {preference: 10, exchange: mail.example.com.}
/// </summary>
public class YamlProvider(string directory) : IProvider
{
    public Task PreflightAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"YAML provider directory not found: {directory}");
        return Task.CompletedTask;
    }

    public Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default)
    {
        var normalized = NormalizeZoneName(zoneName);
        var path = Path.Combine(directory, normalized.TrimEnd('.') + ".yaml");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Zone file not found: {path}");

        var yaml = File.ReadAllText(path);
        var records = ParseZoneYaml(yaml, normalized);

        return Task.FromResult(new DnsZone { Name = normalized, Records = records });
    }

    public Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "YamlProvider is read-only and cannot be used as a sync target.");

    public Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default)
    {
        var zones = Directory.GetFiles(directory, "*.yaml")
            .Select(f => NormalizeZoneName(Path.GetFileNameWithoutExtension(f)))
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(zones);
    }

    public static IReadOnlyList<DnsRecord> ParseZoneYaml(string yaml, string zoneName)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        // Parse as raw dictionary: subdomain → record-or-list
        var raw = deserializer.Deserialize<Dictionary<string, object>>(yaml)
                  ?? new Dictionary<string, object>();

        var flat = new List<DnsRecord>();

        foreach (var (subdomain, value) in raw)
        {
            var fqdn = BuildFqdn(subdomain, zoneName);
            var recordDefs = NormalizeToList(value);

            foreach (var def in recordDefs)
            {
                var record = ParseRecordDef(def, fqdn);
                if (record is not null)
                    flat.Add(record);
            }
        }

        return MergeIntoRRsets(flat);
    }

    internal static IReadOnlyList<DnsRecord> MergeIntoRRsets(List<DnsRecord> flat)
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

    /// <summary>
    /// Build FQDN from subdomain + zone. Empty string or '@' = apex.
    /// </summary>
    private static string BuildFqdn(string subdomain, string zoneName)
    {
        if (string.IsNullOrEmpty(subdomain) || subdomain == "@")
            return zoneName;

        // Already absolute
        if (subdomain.EndsWith('.'))
            return subdomain.ToLowerInvariant();

        return $"{subdomain.ToLowerInvariant()}.{zoneName}";
    }

    /// <summary>
    /// Normalize YAML value to a list of record definition dictionaries.
    /// Handles both:
    ///   - Single record: {type: A, ...}
    ///   - List of records: [{type: A, ...}, {type: MX, ...}]
    /// </summary>
    private static List<Dictionary<object, object>> NormalizeToList(object value)
    {
        if (value is List<object> list)
            return list.OfType<Dictionary<object, object>>().ToList();

        if (value is Dictionary<object, object> dict)
            return [dict];

        return [];
    }

    private static DnsRecord? ParseRecordDef(Dictionary<object, object> def, string fqdn)
    {
        var type = GetString(def, "type")?.ToUpperInvariant();
        if (type is null) return null;

        var ttl = GetInt(def, "ttl") ?? 3600;

        return type switch
        {
            "A" => new ARecord
            {
                Name = fqdn,
                Type = "A",
                Ttl = ttl,
                Addresses = GetStringList(def, "values", "value")
            },
            "AAAA" => new AaaaRecord
            {
                Name = fqdn,
                Type = "AAAA",
                Ttl = ttl,
                Addresses = GetStringList(def, "values", "value")
            },
            "CNAME" => new CnameRecord
            {
                Name = fqdn,
                Type = "CNAME",
                Ttl = ttl,
                Target = NormalizeFqdn(GetString(def, "value") ?? GetString(def, "target") ?? "")
            },
            "MX" => new MxRecord
            {
                Name = fqdn,
                Type = "MX",
                Ttl = ttl,
                Values = GetMxValues(def)
            },
            "TXT" => new TxtRecord
            {
                Name = fqdn,
                Type = "TXT",
                Ttl = ttl,
                Values = GetStringList(def, "values", "value").Select(UnescapeTxt).ToList()
            },
            "NS" => new NsRecord
            {
                Name = fqdn,
                Type = "NS",
                Ttl = ttl,
                Nameservers = GetStringList(def, "values", "value").Select(NormalizeFqdn).ToList()
            },
            "CAA" => new CaaRecord
            {
                Name = fqdn,
                Type = "CAA",
                Ttl = ttl,
                Values = GetCaaValues(def)
            },
            "SRV" => new SrvRecord
            {
                Name = fqdn,
                Type = "SRV",
                Ttl = ttl,
                Values = GetSrvValues(def)
            },
            _ => null  // unknown type, skip
        };
    }

    private static string? GetString(Dictionary<object, object> def, string key) =>
        def.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static int? GetInt(Dictionary<object, object> def, string key) =>
        def.TryGetValue(key, out var v) && v is not null && int.TryParse(v.ToString(), out var i) ? i : null;

    private static IReadOnlyList<string> GetStringList(
        Dictionary<object, object> def, string listKey, string singleKey)
    {
        if (def.TryGetValue(listKey, out var listVal) && listVal is List<object> list)
            return list.Select(v => v?.ToString() ?? "").Where(s => s.Length > 0).ToList();

        var single = GetString(def, singleKey) ?? GetString(def, listKey);
        if (single is not null)
            return [single];

        return [];
    }

    /// <summary>
    /// Some providers (octodns, Porkbun) escape semicolons as \; in TXT record values.
    /// Normalize to plain semicolons so all downstream providers receive clean values.
    /// </summary>
    private static string UnescapeTxt(string value) => value.Replace("\\;", ";");

    private static IReadOnlyList<MxValue> GetMxValues(Dictionary<object, object> def)
    {
        if (!def.TryGetValue("values", out var raw) || raw is not List<object> list)
            return [];

        return list.OfType<Dictionary<object, object>>().Select(v => new MxValue(
            Preference: GetInt(v, "preference") ?? 10,
            Exchange: NormalizeFqdn(GetString(v, "exchange") ?? "")
        )).ToList();
    }

    private static IReadOnlyList<SrvValue> GetSrvValues(Dictionary<object, object> def)
    {
        if (!def.TryGetValue("values", out var raw) || raw is not List<object> list)
            return [];

        return list.OfType<Dictionary<object, object>>().Select(v => new SrvValue(
            Priority: GetInt(v, "priority") ?? 0,
            Weight: GetInt(v, "weight") ?? 0,
            Port: GetInt(v, "port") ?? 0,
            Target: NormalizeFqdn(GetString(v, "target") ?? "")
        )).ToList();
    }

    private static IReadOnlyList<CaaValue> GetCaaValues(Dictionary<object, object> def)
    {
        if (!def.TryGetValue("values", out var raw) || raw is not List<object> list)
            return [];

        return list.OfType<Dictionary<object, object>>().Select(v => new CaaValue(
            Flags: GetInt(v, "flags") ?? 0,
            Tag: GetString(v, "tag") ?? "",
            Value: GetString(v, "value") ?? ""
        )).ToList();
    }

}
