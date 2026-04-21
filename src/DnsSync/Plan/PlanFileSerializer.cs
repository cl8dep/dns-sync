using System.Security.Cryptography;
using System.Text;
using DnsSync.Core;
using DnsSync.Core.Records;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DnsSync.Plan;

public static class PlanFileSerializer
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Builds a SavedPlanBody from the computed zone plans, signs it with HMAC-SHA256
    /// (key = SHA256 of config file bytes), and writes the signed plan to <paramref name="outputPath"/>.
    /// </summary>
    public static void Save(
        string configPath,
        IEnumerable<(string ZoneName, string TargetName, DnsPlan Plan)> zonePlans,
        string outputPath)
    {
        var configBytes = File.ReadAllBytes(configPath);
        var configHashBytes = SHA256.HashData(configBytes);
        var configHashHex = $"sha256:{Convert.ToHexString(configHashBytes).ToLowerInvariant()}";

        var body = new SavedPlanBody
        {
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            ConfigHash = configHashHex,
            Zones = zonePlans
                .Select(z => ToSavedZonePlan(z.ZoneName, z.TargetName, z.Plan))
                .ToList()
        };

        var bodyYaml = YamlSerializer.Serialize(body);
        var signature = ComputeSignature(configHashBytes, bodyYaml);

        var file = new SavedPlanFile { Plan = body, Signature = signature };
        File.WriteAllText(outputPath, YamlSerializer.Serialize(file), Encoding.UTF8);
    }

    /// <summary>
    /// Loads a saved plan file, verifies its signature against the current config file,
    /// and returns the plan body. Throws <see cref="InvalidOperationException"/> if the
    /// config has changed or the signature does not match.
    /// </summary>
    public static SavedPlanBody Load(string configPath, string planPath)
    {
        if (!File.Exists(planPath))
            throw new FileNotFoundException($"Plan file not found: {planPath}");

        var fileYaml = File.ReadAllText(planPath);
        var file = YamlDeserializer.Deserialize<SavedPlanFile>(fileYaml)
            ?? throw new InvalidOperationException("Plan file is empty or invalid.");

        var configBytes = File.ReadAllBytes(configPath);
        var configHashBytes = SHA256.HashData(configBytes);
        var expectedConfigHash = $"sha256:{Convert.ToHexString(configHashBytes).ToLowerInvariant()}";

        if (!string.Equals(file.Plan.ConfigHash, expectedConfigHash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Config file has changed since this plan was generated. " +
                "Re-run 'dns-sync plan --save-plan' to generate a fresh plan.");

        var bodyYaml = YamlSerializer.Serialize(file.Plan);
        var expectedSignature = ComputeSignature(configHashBytes, bodyYaml);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(file.Signature),
                Encoding.UTF8.GetBytes(expectedSignature)))
            throw new InvalidOperationException(
                "Plan file signature verification failed — the file may have been tampered with.");

        return file.Plan;
    }

    /// <summary>Reconstructs a <see cref="DnsPlan"/> from a <see cref="SavedZonePlan"/>.</summary>
    public static DnsPlan ToDnsPlan(SavedZonePlan savedZone)
    {
        var changes = savedZone.Changes.Select(c => new RecordChange
        {
            ChangeType = c.Type.ToLowerInvariant() switch
            {
                "create" => ChangeType.Create,
                "update" => ChangeType.Update,
                "delete" => ChangeType.Delete,
                _ => throw new InvalidOperationException($"Unknown change type '{c.Type}' in plan file.")
            },
            Before = c.Before is null ? null : ToRecord(c.Name, c.RecordType, c.Before),
            After  = c.After  is null ? null : ToRecord(c.Name, c.RecordType, c.After),
        }).ToList();

        return new DnsPlan { Changes = changes };
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string ComputeSignature(byte[] keyBytes, string bodyYaml)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(bodyYaml);
        var sigBytes = HMACSHA256.HashData(keyBytes, bodyBytes);
        return $"sha256:{Convert.ToHexString(sigBytes).ToLowerInvariant()}";
    }

    private static SavedZonePlan ToSavedZonePlan(string zoneName, string targetName, DnsPlan plan) =>
        new()
        {
            Zone = zoneName,
            Target = targetName,
            Changes = plan.Changes.Select(ToSavedChange).ToList()
        };

    private static SavedChange ToSavedChange(RecordChange c) =>
        new()
        {
            Type = c.ChangeType.ToString().ToLowerInvariant(),
            Name = c.RecordName,
            RecordType = c.RecordType,
            Before = c.Before is null ? null : ToSavedRecord(c.Before),
            After  = c.After  is null ? null : ToSavedRecord(c.After),
        };

    private static SavedRecord ToSavedRecord(DnsRecord record) =>
        new()
        {
            Ttl = record.Ttl,
            Values = EncodeValues(record)
        };

    private static List<string> EncodeValues(DnsRecord record) => record switch
    {
        ARecord r     => [..r.Addresses],
        AaaaRecord r  => [..r.Addresses],
        CnameRecord r => [r.Target],
        NsRecord r    => [..r.Nameservers],
        TxtRecord r   => [..r.Values],
        MxRecord r    => r.Values.OrderBy(v => v.Preference)
                            .Select(v => $"{v.Preference} {v.Exchange}").ToList(),
        SrvRecord r   => r.Values.OrderBy(v => v.Priority)
                            .Select(v => $"{v.Priority} {v.Weight} {v.Port} {v.Target}").ToList(),
        CaaRecord r   => r.Values.OrderBy(v => v.Tag)
                            .Select(v => $"{v.Flags} {v.Tag} {v.Value}").ToList(),
        _ => throw new InvalidOperationException($"Unsupported record type: {record.GetType().Name}")
    };

    private static DnsRecord ToRecord(string name, string type, SavedRecord saved) =>
        type.ToUpperInvariant() switch
        {
            "A"    => new ARecord    { Name = name, Type = type, Ttl = saved.Ttl, Addresses   = saved.Values },
            "AAAA" => new AaaaRecord { Name = name, Type = type, Ttl = saved.Ttl, Addresses   = saved.Values },
            "NS"   => new NsRecord   { Name = name, Type = type, Ttl = saved.Ttl, Nameservers = saved.Values },
            "TXT"  => new TxtRecord  { Name = name, Type = type, Ttl = saved.Ttl, Values      = saved.Values },
            "CNAME" => new CnameRecord { Name = name, Type = type, Ttl = saved.Ttl,
                           Target = saved.Values.FirstOrDefault() ?? string.Empty },
            "MX" => new MxRecord { Name = name, Type = type, Ttl = saved.Ttl,
                        Values = saved.Values.Select(ParseMx).ToList() },
            "SRV" => new SrvRecord { Name = name, Type = type, Ttl = saved.Ttl,
                         Values = saved.Values.Select(ParseSrv).ToList() },
            "CAA" => new CaaRecord { Name = name, Type = type, Ttl = saved.Ttl,
                         Values = saved.Values.Select(ParseCaa).ToList() },
            _ => throw new InvalidOperationException($"Unsupported record type in plan file: {type}")
        };

    private static MxValue ParseMx(string s)
    {
        var parts = s.Split(' ', 2);
        return new MxValue(int.Parse(parts[0]), parts[1]);
    }

    private static SrvValue ParseSrv(string s)
    {
        var parts = s.Split(' ', 4);
        return new SrvValue(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), parts[3]);
    }

    private static CaaValue ParseCaa(string s)
    {
        var parts = s.Split(' ', 3);
        return new CaaValue(int.Parse(parts[0]), parts[1], parts[2]);
    }
}
