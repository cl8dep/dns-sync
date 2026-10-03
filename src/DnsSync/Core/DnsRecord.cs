namespace DnsSync.Core;

/// <summary>
/// Base class for all DNS record types. Name is always stored as FQDN with trailing dot.
/// </summary>
public abstract class DnsRecord
{
    public required string Name { get; init; }   // e.g. "www.example.com."
    public required string Type { get; init; }   // e.g. "A", "MX", "CNAME"
    public required int Ttl { get; init; }

    /// <summary>
    /// Cloudflare proxy status. Null means unmanaged: the flag is neither compared nor written.
    /// Only A, AAAA and CNAME records can be proxied; providers other than Cloudflare ignore it.
    /// </summary>
    public bool? Proxied { get => _proxied; init => _proxied = value; }
    private bool? _proxied;

    /// <summary>Copy of this record with the proxy status unmanaged.</summary>
    public DnsRecord WithoutProxied()
    {
        var copy = (DnsRecord)MemberwiseClone();
        copy._proxied = null;
        return copy;
    }

    /// <summary>True when both records declare a proxy status and they disagree.</summary>
    public static bool ProxiedDiffers(DnsRecord a, DnsRecord b) =>
        a.Proxied is not null && b.Proxied is not null && a.Proxied != b.Proxied;

    protected string ProxiedSuffix => Proxied == true ? " (proxied)" : "";

    /// <summary>Stable hash of the record's values for diff comparison. Excludes TTL.</summary>
    public abstract string CanonicalHash();

    /// <summary>Human-readable values for plan output.</summary>
    public abstract string FormatValues();

    /// <summary>Normalize a domain name to FQDN with trailing dot, lowercase.</summary>
    protected static string NormalizeFqdn(string value) => DnsNameHelper.NormalizeFqdn(value);

    /// <summary>
    /// Join and re-split TXT strings to normalize provider-specific chunking.
    /// RFC 1035 limits TXT RDATA strings to 255 bytes; providers split or join differently.
    /// </summary>
    protected static string NormalizeTxt(IEnumerable<string> parts) =>
        string.Concat(parts);
}
