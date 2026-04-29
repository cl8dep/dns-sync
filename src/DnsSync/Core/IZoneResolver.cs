using DnsSync.Config;

namespace DnsSync.Core;

public interface IZoneResolver
{
    /// <summary>
    /// Returns the merged set of zones to process: explicit <c>zones:</c> entries take
    /// precedence over zones discovered via <c>zone_groups:</c>. Emits a warning for any
    /// zone_group zone that is overridden by an explicit zone entry.
    /// </summary>
    Task<IReadOnlyDictionary<string, ZoneConfig>> ResolveAsync(
        DnsSyncConfig config, CancellationToken cancellationToken);
}
