namespace DnsSync.Core;

public static class DnsNameHelper
{
    public static string NormalizeFqdn(string value)
    {
        var lower = value.ToLowerInvariant().Trim();
        return lower.EndsWith('.') ? lower : lower + ".";
    }

    public static string NormalizeZoneName(string name)
    {
        var lower = name.ToLowerInvariant().Trim();
        return lower.EndsWith('.') ? lower : lower + ".";
    }
}
