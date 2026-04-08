namespace DnsSync.Core.Records;

public class ARecord : DnsRecord
{
    public required IReadOnlyList<string> Addresses { get; init; }

    public override string CanonicalHash() =>
        string.Join(",", Addresses.OrderBy(x => x));

    public override string FormatValues() =>
        string.Join(", ", Addresses.OrderBy(x => x));
}
