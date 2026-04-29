using DnsSync.Config;
using DnsSync.Core;
using DnsSync.Providers;
using Microsoft.Extensions.Logging;

namespace DnsSync.Tests.Helpers;

/// <summary>
/// Configurable stub for IProvider.
/// Pre-load zones via <see cref="SetZone"/> and apply results via <see cref="SetApplyResult"/>.
/// </summary>
internal sealed class StubProvider : IProvider
{
    private readonly Dictionary<string, DnsZone> _zones = new(StringComparer.OrdinalIgnoreCase);
    private ApplyResult _applyResult = new(0, 0, []);

    public List<string> PreflightCalls { get; } = [];
    public List<(string Zone, DnsPlan Plan)> ApplyCalls { get; } = [];
    public Exception? PreflightException { get; set; }

    public void SetZone(DnsZone zone) => _zones[zone.Name] = zone;

    public void SetApplyResult(ApplyResult result) => _applyResult = result;

    public Task PreflightAsync(CancellationToken ct = default)
    {
        if (PreflightException is not null) throw PreflightException;
        PreflightCalls.Add("preflight");
        return Task.CompletedTask;
    }

    public Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default)
    {
        if (_zones.TryGetValue(zoneName, out var zone))
            return Task.FromResult(zone);
        return Task.FromResult(new DnsZone { Name = zoneName, Records = [] });
    }

    public Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(_zones.Keys.ToList());

    public Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default)
    {
        ApplyCalls.Add((zoneName, plan));
        return Task.FromResult(_applyResult);
    }
}

/// <summary>
/// IProviderFactory stub that returns a pre-configured StubProvider for any name.
/// Use <see cref="SetProvider"/> to supply a specific stub per provider name,
/// or set <see cref="Default"/> for a catch-all.
/// </summary>
internal sealed class StubProviderFactory : IProviderFactory
{
    private readonly Dictionary<string, IProvider> _map = new(StringComparer.OrdinalIgnoreCase);

    public StubProvider Default { get; } = new();

    public void SetProvider(string name, IProvider provider) => _map[name] = provider;

    public IProvider Create(string name, ProviderConfig config, ILoggerFactory loggerFactory)
        => _map.TryGetValue(name, out var p) ? p : Default;
}
