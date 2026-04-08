namespace DnsSync.Core.Records;

public class SrvRecord : DnsRecord
{
    public required IReadOnlyList<SrvValue> Values { get; init; }

    public override string CanonicalHash() =>
        string.Join("|", Values
            .OrderBy(v => v.Priority)
            .ThenBy(v => v.Weight)
            .ThenBy(v => v.Port)
            .ThenBy(v => v.Target)
            .Select(v => $"{v.Priority}:{v.Weight}:{v.Port}:{NormalizeFqdn(v.Target)}"));

    public override string FormatValues() =>
        string.Join(", ", Values
            .OrderBy(v => v.Priority)
            .Select(v => $"{v.Priority} {v.Weight} {v.Port} {v.Target}"));
}

public record SrvValue(int Priority, int Weight, int Port, string Target);
