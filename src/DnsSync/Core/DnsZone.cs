namespace DnsSync.Core;

/// <summary>A collection of DNS records for a single zone.</summary>
public class DnsZone
{
    public required string Name { get; init; }  // e.g. "example.com."
    public required IReadOnlyList<DnsRecord> Records { get; init; }

    /// <summary>Records keyed by (name, type) for diff lookup.</summary>
    public ILookup<(string Name, string Type), DnsRecord> ByNameType =>
        Records.ToLookup(r => (r.Name, r.Type));
}
