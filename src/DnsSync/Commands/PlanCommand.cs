using System.Text.Json;
using DnsSync.Core;
using DnsSync.Providers;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class PlanCommand(ILoggerFactory loggerFactory) : AsyncCommand<PlanSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PlanSettings settings, CancellationToken cancellationToken)
    {
        var jsonMode = string.Equals(settings.Output, "json", StringComparison.OrdinalIgnoreCase);
        var useSpinners = !jsonMode && !settings.GcpLogs && !settings.Verbose;

        try
        {
            var config = CommandHelpers.LoadAndValidateConfig(settings);

            if (!jsonMode)
                AnsiConsole.MarkupLine("\nRunning pre-flight checks...");

            var totalChanges = 0;
            var hasErrors = false;

            // JSON output accumulator
            var jsonZones = new List<object>();

            foreach (var (zoneName, zoneConfig) in config.Zones)
            {
                var sourceProvider = ProviderFactory.Create(
                    zoneConfig.Source, config.Providers[zoneConfig.Source], loggerFactory);

                if (useSpinners)
                {
                    await AnsiConsole.Status().StartAsync(
                        $"Checking source '{zoneConfig.Source}'...",
                        async ctx => { ctx.Spinner(Spinner.Known.Dots); await sourceProvider.PreflightAsync(cancellationToken); });
                }
                else
                {
                    await sourceProvider.PreflightAsync(cancellationToken);
                }

                if (!jsonMode)
                    AnsiConsole.MarkupLine($"[green]✓[/] Source provider '{Markup.Escape(zoneConfig.Source)}' reachable");

                DnsZone sourceZone;
                if (useSpinners)
                {
                    sourceZone = await AnsiConsole.Status().StartAsync(
                        $"Fetching {zoneName} from '{zoneConfig.Source}'...",
                        async ctx => { ctx.Spinner(Spinner.Known.Dots); return await sourceProvider.GetZoneAsync(zoneName, cancellationToken); });
                }
                else
                {
                    sourceZone = await sourceProvider.GetZoneAsync(zoneName, cancellationToken);
                }

                foreach (var targetName in zoneConfig.Targets)
                {
                    var targetProvider = ProviderFactory.Create(
                        targetName, config.Providers[targetName], loggerFactory);

                    try
                    {
                        if (useSpinners)
                        {
                            await AnsiConsole.Status().StartAsync(
                                $"Checking target '{targetName}'...",
                                async ctx => { ctx.Spinner(Spinner.Known.Dots); await targetProvider.PreflightAsync(cancellationToken); });
                        }
                        else
                        {
                            await targetProvider.PreflightAsync(cancellationToken);
                        }

                        if (!jsonMode)
                            AnsiConsole.MarkupLine($"[green]✓[/] Target provider '{Markup.Escape(targetName)}' reachable");

                        DnsZone targetZone;
                        if (useSpinners)
                        {
                            targetZone = await AnsiConsole.Status().StartAsync(
                                $"Fetching {zoneName} from '{targetName}'...",
                                async ctx => { ctx.Spinner(Spinner.Known.Dots); return await targetProvider.GetZoneAsync(zoneName, cancellationToken); });
                        }
                        else
                        {
                            targetZone = await targetProvider.GetZoneAsync(zoneName, cancellationToken);
                        }

                        var plan = ZoneDiff.Diff(sourceZone, targetZone, settings.IncludeApexNs);

                        if (jsonMode)
                        {
                            jsonZones.Add(BuildJsonPlan(zoneName, targetName, plan));
                        }
                        else
                        {
                            CommandHelpers.PrintPlan(plan, zoneName, targetName, settings.Wide);
                        }

                        totalChanges += plan.Total;
                    }
                    catch (Exception ex)
                    {
                        if (!jsonMode)
                            AnsiConsole.MarkupLine(
                                $"[red]✗[/] Target '{Markup.Escape(targetName)}': {Markup.Escape(ex.Message)}");
                        hasErrors = true;
                    }
                }
            }

            if (jsonMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(jsonZones, new JsonSerializerOptions { WriteIndented = true }));
                return hasErrors ? 1 : (settings.ExitCode && totalChanges > 0 ? 2 : 0);
            }

            if (totalChanges > 0)
                AnsiConsole.MarkupLine(
                    $"\n[bold]{totalChanges} total change(s)[/] — run [bold]dns-sync apply[/] to apply.");
            else if (!hasErrors)
                AnsiConsole.MarkupLine("\n[green]✓ All zones are in sync. No changes needed.[/]");

            return hasErrors ? 1 : (settings.ExitCode && totalChanges > 0 ? 2 : 0);
        }
        catch (Exception ex)
        {
            CommandHelpers.PrintError(ex, settings.Verbose);
            return 1;
        }
    }

    private static object BuildJsonPlan(string zoneName, string targetName, DnsPlan plan)
    {
        var changes = plan.Changes.Select(c => new
        {
            type = c.ChangeType.ToString().ToLowerInvariant(),
            record_name = (c.After ?? c.Before)?.Name,
            record_type = (c.After ?? c.Before)?.Type,
            before = c.Before?.FormatValues(),
            after = c.After?.FormatValues()
        }).ToList();

        return new
        {
            zone = zoneName,
            target = targetName,
            creates = plan.Creates,
            updates = plan.Updates,
            deletes = plan.Deletes,
            changes
        };
    }
}
