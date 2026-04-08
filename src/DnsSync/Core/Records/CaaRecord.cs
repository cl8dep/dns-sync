namespace DnsSync.Core.Records;

public class CaaRecord : DnsRecord
{
    public required IReadOnlyList<CaaValue> Values { get; init; }

    public override string CanonicalHash() =>
        string.Join("|", Values
            .OrderBy(v => v.Flags)
            .ThenBy(v => v.Tag)
            .ThenBy(v => v.Value)
            .Select(v => $"{v.Flags}:{v.Tag}:{v.Value}"));

    public override string FormatValues() =>
        string.Join(", ", Values
            .OrderBy(v => v.Tag)
            .Select(v => $"{v.Flags} {v.Tag} \"{v.Value}\""));
}

public record CaaValue(int Flags, string Tag, string Value);
