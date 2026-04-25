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

    /// <summary>
    /// Parse a TXT record content field as returned by DNS providers.
    /// Handles quoted strings ("value"), backslash escapes, and RFC 4408
    /// multi-chunk values ("chunk1" "chunk2" → "chunk1chunk2").
    /// </summary>
    internal static string ParseTxtContent(string content)
    {
        // If not quoted, return as-is (Porkbun sometimes returns raw unquoted values)
        var trimmed = content.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '"') return content;

        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < content.Length)
        {
            while (i < content.Length && content[i] == ' ') i++;
            if (i >= content.Length) break;

            if (content[i] == '"')
            {
                i++; // skip opening quote
                while (i < content.Length && content[i] != '"')
                {
                    if (content[i] == '\\' && i + 1 < content.Length)
                    {
                        result.Append(content[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        result.Append(content[i]);
                        i++;
                    }
                }
                if (i < content.Length) i++; // skip closing quote
            }
            else
            {
                var start = i;
                while (i < content.Length && content[i] != ' ') i++;
                result.Append(content[start..i]);
            }
        }
        return result.ToString();
    }
}
