using System.Net;
using System.Net.Sockets;
using DnsSync.Core;
using DnsSync.Core.Records;

namespace DnsSync.Validation;

public static class ZoneValidator
{
    private static readonly HashSet<string> KnownTypes =
        new(StringComparer.OrdinalIgnoreCase)
        { "A", "AAAA", "CNAME", "MX", "TXT", "NS", "CAA", "SRV", "SOA" };

    private static readonly HashSet<string> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase)
        { "A", "AAAA", "CNAME", "MX", "TXT", "NS", "CAA", "SRV" };

    public static ValidationResult Validate(DnsZone zone)
    {
        var result = new ValidationResult();

        var namesSeen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in zone.Records)
        {
            // Track (name → set of types) for CNAME conflict detection
            if (!namesSeen.TryGetValue(record.Name, out var types))
            {
                types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                namesSeen[record.Name] = types;
            }

            // Validate record type
            if (!KnownTypes.Contains(record.Type))
                result.AddWarning($"{record.Name} {record.Type}: unknown record type, will be skipped");
            else if (!SupportedTypes.Contains(record.Type))
                result.AddWarning($"{record.Name} {record.Type}: record type not supported, will be skipped");

            // Validate TTL
            if (record.Ttl < 0)
                result.AddError($"{record.Name} {record.Type}: TTL must be >= 0 (got {record.Ttl})");
            else if (record.Ttl > 2_147_483_647)
                result.AddError($"{record.Name} {record.Type}: TTL exceeds max value");
            else if (record.Ttl < 60)
                result.AddWarning($"{record.Name} {record.Type}: very low TTL ({record.Ttl}s) may cause excessive DNS traffic");

            // Validate FQDN format
            if (!record.Name.EndsWith('.'))
                result.AddError($"Record name '{record.Name}' is not a valid FQDN (missing trailing dot)");

            // Type-specific validation
            switch (record)
            {
                case CnameRecord cname:
                    if (string.IsNullOrWhiteSpace(cname.Target) || cname.Target == ".")
                        result.AddError($"{record.Name} CNAME: target value is empty");
                    else if (!IsValidHostname(cname.Target))
                        result.AddError($"{record.Name} CNAME: '{cname.Target}' is not a valid hostname");
                    types.Add("CNAME");
                    break;

                case ARecord a:
                    if (a.Addresses.Count == 0)
                        result.AddError($"{record.Name} A: no addresses defined");
                    foreach (var addr in a.Addresses)
                    {
                        if (!IPAddress.TryParse(addr, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                            result.AddError($"{record.Name} A: '{addr}' is not a valid IPv4 address");
                    }
                    types.Add("A");
                    break;

                case AaaaRecord aaaa:
                    if (aaaa.Addresses.Count == 0)
                        result.AddError($"{record.Name} AAAA: no addresses defined");
                    foreach (var addr in aaaa.Addresses)
                    {
                        if (!IPAddress.TryParse(addr, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                            result.AddError($"{record.Name} AAAA: '{addr}' is not a valid IPv6 address");
                    }
                    types.Add("AAAA");
                    break;

                case MxRecord mx:
                    if (mx.Values.Count == 0)
                        result.AddError($"{record.Name} MX: no values defined");
                    types.Add("MX");
                    break;

                case TxtRecord txt:
                    if (txt.Values.Count == 0)
                        result.AddError($"{record.Name} TXT: no values defined");
                    types.Add("TXT");
                    break;

                case NsRecord ns:
                    if (ns.Nameservers.Count == 0)
                        result.AddError($"{record.Name} NS: no nameservers defined");
                    types.Add("NS");
                    break;

                case CaaRecord caa:
                    if (caa.Values.Count == 0)
                        result.AddError($"{record.Name} CAA: no values defined");
                    types.Add("CAA");
                    break;

                case SrvRecord srv:
                    if (srv.Values.Count == 0)
                        result.AddError($"{record.Name} SRV: no values defined");
                    foreach (var v in srv.Values)
                    {
                        if (v.Port is < 0 or > 65535)
                            result.AddError($"{record.Name} SRV: port {v.Port} is out of range (0–65535)");
                    }
                    types.Add("SRV");
                    break;

                default:
                    types.Add(record.Type);
                    break;
            }
        }

        // CNAME conflict check: CNAME cannot coexist with other record types at the same name
        foreach (var (name, types) in namesSeen)
        {
            if (types.Contains("CNAME") && types.Count > 1)
                result.AddError(
                    $"{name}: CNAME cannot coexist with other record types " +
                    $"({string.Join(", ", types.Where(t => t != "CNAME"))}) — RFC 1034 violation");
        }

        return result;
    }

    private static bool IsValidHostname(string value)
    {
        var trimmed = value.TrimEnd('.');
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= 253
            && trimmed.Split('.').All(label =>
                label.Length is > 0 and <= 63
                && label.All(c => char.IsLetterOrDigit(c) || c == '-')
                && !label.StartsWith('-') && !label.EndsWith('-'));
    }
}

public class ValidationResult
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;

    public bool IsValid => _errors.Count == 0;

    public void AddError(string message) => _errors.Add(message);
    public void AddWarning(string message) => _warnings.Add(message);

    public bool IsValidStrict => _errors.Count == 0 && _warnings.Count == 0;
}
