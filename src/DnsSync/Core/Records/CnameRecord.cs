namespace DnsSync.Core.Records;

public class CnameRecord : DnsRecord
{
    public required string Target { get; init; }  // FQDN with trailing dot

    public override string CanonicalHash() => NormalizeFqdn(Target);

    public override string FormatValues() => NormalizeFqdn(Target);
}
