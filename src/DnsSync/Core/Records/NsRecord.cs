namespace DnsSync.Core.Records;

public class NsRecord : DnsRecord
{
    public required IReadOnlyList<string> Nameservers { get; init; }

    public override string CanonicalHash() =>
        string.Join(",", Nameservers.Select(NormalizeFqdn).OrderBy(x => x));

    public override string FormatValues() =>
        string.Join(", ", Nameservers.Select(NormalizeFqdn).OrderBy(x => x));
}
