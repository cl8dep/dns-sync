using DnsSync.Config;
using DnsSync.Core;

namespace DnsSync.Tests.Helpers;

/// <summary>
/// IZoneResolver stub. Optionally pre-loaded with zones; otherwise returns empty.
/// </summary>
internal sealed class StubZoneResolver : IZoneResolver
{
    private readonly IReadOnlyDictionary<string, ZoneConfig> _zones;

    public StubZoneResolver(IReadOnlyDictionary<string, ZoneConfig>? zones = null)
    {
        _zones = zones ?? new Dictionary<string, ZoneConfig>(StringComparer.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyDictionary<string, ZoneConfig>> ResolveAsync(
        DnsSyncConfig config, CancellationToken cancellationToken = default)
        => Task.FromResult(_zones);
}
