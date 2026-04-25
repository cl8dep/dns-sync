namespace DnsSync.Core.Records;

public class TxtRecord : DnsRecord
{
    /// <summary>
    /// Each string is one TXT record value. Multiple values = multiple strings in the RRset.
    /// Chunks within a single value are joined (providers split at 255 bytes differently).
    /// </summary>
    public required IReadOnlyList<string> Values { get; init; }

    public override string CanonicalHash() =>
        string.Join("|", Values.Select(NormalizeTxtValue).OrderBy(v => v));

    public override string FormatValues() =>
        string.Join(" | ", Values.OrderBy(v => v).Select(v => $"\"{v}\""));

    /// <summary>
    /// Normalize TXT value for comparison: unescape BIND-style escape sequences
    /// (e.g. \; → ;, \\ → \) so values from YAML zone files and from providers
    /// compare equal regardless of escaping convention.
    /// </summary>
    internal static string NormalizeTxtValue(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\\(.)", "$1");
}
