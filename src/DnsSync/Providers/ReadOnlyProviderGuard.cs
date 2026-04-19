using DnsSync.Core;

namespace DnsSync.Providers;

/// <summary>
/// Wraps a provider marked as readonly: true in config.
/// Delegates all read operations; blocks ApplyPlanAsync at the provider level.
/// </summary>
public class ReadOnlyProviderGuard(IProvider inner, string providerName) : IProvider
{
    public Task PreflightAsync(CancellationToken ct = default) =>
        inner.PreflightAsync(ct);

    public Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default) =>
        inner.GetZoneAsync(zoneName, ct);

    public Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default) =>
        inner.GetZonesAsync(ct);

    public Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Provider '{providerName}' is marked as read-only and cannot be used as a sync target.");
}
