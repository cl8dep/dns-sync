using System.Text;
using DnsSync.Core;
using DnsSync.Core.Records;

namespace DnsSync.Providers.Yaml;

/// <summary>
/// Serializes a DnsZone to the YAML format consumed by YamlProvider.
/// Output is human-readable and can be round-tripped through YamlProvider.ParseZoneYaml.
/// </summary>
public static class ZoneYamlSerializer
{
    public static string Serialize(DnsZone zone)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Zone: {zone.Name}");
        sb.AppendLine($"# Imported by dns-sync on {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();

        // Group records by subdomain key for output
        var bySubdomain = zone.Records
            .GroupBy(r => SubdomainKey(r.Name, zone.Name))
            .OrderBy(g => g.Key == "" ? "\0" : g.Key); // apex first

        foreach (var group in bySubdomain)
        {
            var key = group.Key;
            var records = group.ToList();
            var yamlKey = key == "" ? "''" : key;

            if (records.Count == 1)
            {
                sb.AppendLine($"{yamlKey}:");
                AppendRecord(sb, records[0], indent: "  ");
            }
            else
            {
                sb.AppendLine($"{yamlKey}:");
                foreach (var record in records)
                {
                    sb.AppendLine("  -");
                    AppendRecord(sb, record, indent: "    ");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendRecord(StringBuilder sb, DnsRecord record, string indent)
    {
        sb.AppendLine($"{indent}type: {record.Type}");
        sb.AppendLine($"{indent}ttl: {record.Ttl}");

        switch (record)
        {
            case ARecord a:
                AppendStringList(sb, a.Addresses, indent);
                break;

            case AaaaRecord aaaa:
                AppendStringList(sb, aaaa.Addresses, indent);
                break;

            case CnameRecord cname:
                sb.AppendLine($"{indent}value: {cname.Target}");
                break;

            case MxRecord mx:
                sb.AppendLine($"{indent}values:");
                foreach (var v in mx.Values)
                {
                    sb.AppendLine($"{indent}  - preference: {v.Preference}");
                    sb.AppendLine($"{indent}    exchange: {v.Exchange}");
                }
                break;

            case TxtRecord txt:
                if (txt.Values.Count == 1)
                    sb.AppendLine($"{indent}value: {QuoteTxt(txt.Values[0])}");
                else
                    AppendQuotedStringList(sb, txt.Values, indent);
                break;

            case NsRecord ns:
                AppendStringList(sb, ns.Nameservers, indent);
                break;

            case CaaRecord caa:
                sb.AppendLine($"{indent}values:");
                foreach (var v in caa.Values)
                {
                    sb.AppendLine($"{indent}  - flags: {v.Flags}");
                    sb.AppendLine($"{indent}    tag: {v.Tag}");
                    sb.AppendLine($"{indent}    value: \"{v.Value}\"");
                }
                break;

            case SrvRecord srv:
                sb.AppendLine($"{indent}values:");
                foreach (var v in srv.Values)
                {
                    sb.AppendLine($"{indent}  - priority: {v.Priority}");
                    sb.AppendLine($"{indent}    weight: {v.Weight}");
                    sb.AppendLine($"{indent}    port: {v.Port}");
                    sb.AppendLine($"{indent}    target: {v.Target}");
                }
                break;
        }
    }

    private static void AppendStringList(StringBuilder sb, IReadOnlyList<string> values, string indent)
    {
        if (values.Count == 1)
        {
            sb.AppendLine($"{indent}value: {values[0]}");
            return;
        }

        sb.AppendLine($"{indent}values:");
        foreach (var v in values)
            sb.AppendLine($"{indent}  - {v}");
    }

    private static void AppendQuotedStringList(StringBuilder sb, IReadOnlyList<string> values, string indent)
    {
        sb.AppendLine($"{indent}values:");
        foreach (var v in values)
            sb.AppendLine($"{indent}  - {QuoteTxt(v)}");
    }

    private static string QuoteTxt(string value)
    {
        // Wrap in double quotes if contains spaces or special chars, escaping inner quotes
        if (value.Contains(' ') || value.Contains('"') || value.Contains('\\') || value.Contains(':'))
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        return value;
    }

    private static string SubdomainKey(string fqdn, string zoneName)
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
