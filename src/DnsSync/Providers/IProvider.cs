using DnsSync.Core;

namespace DnsSync.Providers;

public interface IProvider
{
    /// <summary>Read all records for a zone from this provider.</summary>
    Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default);

    /// <summary>Apply a plan (creates/updates/deletes) to this provider.</summary>
    Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default);

    /// <summary>List zones this provider manages (used by dump command).</summary>
    Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default);

    /// <summary>Verify the provider is reachable and credentials are valid.</summary>
    Task PreflightAsync(CancellationToken ct = default);
}

public record ApplyResult(int Applied, int Failed, IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Failed == 0;
}
