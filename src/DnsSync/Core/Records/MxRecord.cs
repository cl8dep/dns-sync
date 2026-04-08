namespace DnsSync.Core.Records;

public class MxRecord : DnsRecord
{
    public required IReadOnlyList<MxValue> Values { get; init; }

    public override string CanonicalHash() =>
        string.Join("|", Values
            .OrderBy(v => v.Preference)
            .ThenBy(v => v.Exchange)
            .Select(v => $"{v.Preference}:{NormalizeFqdn(v.Exchange)}"));

    public override string FormatValues() =>
        string.Join(", ", Values
            .OrderBy(v => v.Preference)
            .Select(v => $"{v.Preference} {NormalizeFqdn(v.Exchange)}"));
}

public record MxValue(int Preference, string Exchange);
