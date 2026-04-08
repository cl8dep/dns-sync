namespace DnsSync.Core.Records;

public class AaaaRecord : DnsRecord
{
    public required IReadOnlyList<string> Addresses { get; init; }

    public override string CanonicalHash() =>
        string.Join(",", Addresses.Select(a => a.ToLowerInvariant()).OrderBy(x => x));

    public override string FormatValues() =>
        string.Join(", ", Addresses.Select(a => a.ToLowerInvariant()).OrderBy(x => x));
}
