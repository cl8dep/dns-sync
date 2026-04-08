namespace DnsSync.Core;

public enum ChangeType { Create, Update, Delete }

public class RecordChange
{
    public required ChangeType ChangeType { get; init; }
    public DnsRecord? Before { get; init; }  // null for Create
    public DnsRecord? After { get; init; }   // null for Delete

    public string RecordName => (After ?? Before)!.Name;
    public string RecordType => (After ?? Before)!.Type;

    /// <summary>True when only TTL differs (values are identical).</summary>
    public bool IsTtlOnlyChange =>
        ChangeType == ChangeType.Update &&
        Before is not null && After is not null &&
        Before.CanonicalHash() == After.CanonicalHash() &&
        Before.Ttl != After.Ttl;
}
