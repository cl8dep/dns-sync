using System.Text.RegularExpressions;
using DnsSync.Config;
using DnsSync.Providers;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DnsSync.Core;

public class ZoneResolver(ILoggerFactory loggerFactory) : IZoneResolver
{
    public async Task<IReadOnlyDictionary<string, ZoneConfig>> ResolveAsync(
        DnsSyncConfig config, CancellationToken cancellationToken)
    {
        // Start with explicit zones — they always win.
        var resolved = new Dictionary<string, ZoneConfig>(
            config.Zones, StringComparer.OrdinalIgnoreCase);

        if (config.ZoneGroups.Count == 0)
            return resolved;

        foreach (var (groupName, group) in config.ZoneGroups)
        {
            var provider = ProviderFactory.Create(
                group.Source, config.Providers[group.Source], loggerFactory);

            IReadOnlyList<string> discovered;
            try
            {
                discovered = await provider.GetZonesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠[/] Zone group '{Markup.Escape(groupName)}': " +
                    $"failed to discover zones from '{Markup.Escape(group.Source)}': {Markup.Escape(ex.Message)}");
                continue;
            }

            Regex? includeRx = group.IncludePattern is not null
                ? new Regex(group.IncludePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)
                : null;
            Regex? excludeRx = group.ExcludePattern is not null
                ? new Regex(group.ExcludePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)
                : null;

            foreach (var zoneName in discovered)
            {
                if (includeRx is not null && !includeRx.IsMatch(zoneName))
                    continue;
                if (excludeRx is not null && excludeRx.IsMatch(zoneName))
                    continue;

                if (resolved.ContainsKey(zoneName))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]~[/] Zone '{Markup.Escape(zoneName)}' from group " +
                        $"'{Markup.Escape(groupName)}' overridden by explicit zones: entry");
                    continue;
                }

                resolved[zoneName] = new ZoneConfig
                {
                    Source = group.Source,
                    Targets = group.Targets,
                };
            }
        }

        return resolved;
    }
}
