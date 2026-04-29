using System.Text.Json;
using DnsSync.Core;
using DnsSync.Providers;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class DriftCommand(ILoggerFactory loggerFactory, IZoneResolver zoneResolver) : AsyncCommand<DriftSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, DriftSettings settings, CancellationToken cancellationToken)
    {
        var output = settings.Output.ToLowerInvariant();
        if (output is not ("color" or "plain" or "json" or "silent"))
            throw new InvalidOperationException(
                $"Unknown --output value '{settings.Output}'. Valid values: color, plain, json, silent.");

        var jsonMode = output == "json";
        var silent = output == "silent";
        var useSpinners = !jsonMode && !silent && !settings.GcpLogs && !settings.Verbose;

        try
        {
            var config = CommandHelpers.LoadAndValidateConfig(settings);

            var driftDetected = false;
            var hasErrors = false;
            var jsonZones = new List<object>();

            var zones = await zoneResolver.ResolveAsync(config, cancellationToken);
            foreach (var (zoneName, zoneConfig) in zones)
            {
                var sourceProvider = ProviderFactory.Create(
                    zoneConfig.Source, config.Providers[zoneConfig.Source], loggerFactory);

                DnsZone sourceZone;
                if (useSpinners)
                {
                    sourceZone = await AnsiConsole.Status().StartAsync(
                        $"Fetching {zoneName} from '{zoneConfig.Source}'...",
                        async ctx =>
                        {
                            ctx.Spinner(Spinner.Known.Dots);
                            await sourceProvider.PreflightAsync(cancellationToken);
                            return await sourceProvider.GetZoneAsync(zoneName, cancellationToken);
                        });
                }
                else
                {
                    await sourceProvider.PreflightAsync(cancellationToken);
                    sourceZone = await sourceProvider.GetZoneAsync(zoneName, cancellationToken);
                }

                foreach (var targetName in zoneConfig.Targets)
                {
                    var targetProvider = ProviderFactory.Create(
                        targetName, config.Providers[targetName], loggerFactory);

                    try
                    {
                        DnsZone targetZone;
                        if (useSpinners)
                        {
                            targetZone = await AnsiConsole.Status().StartAsync(
                                $"Checking {zoneName} against '{targetName}'...",
                                async ctx =>
                                {
                                    ctx.Spinner(Spinner.Known.Dots);
                                    await targetProvider.PreflightAsync(cancellationToken);
                                    return await targetProvider.GetZoneAsync(zoneName, cancellationToken);
                                });
                        }
                        else
                        {
                            await targetProvider.PreflightAsync(cancellationToken);
                            targetZone = await targetProvider.GetZoneAsync(zoneName, cancellationToken);
                        }

                        var plan = ZoneDiff.Diff(sourceZone, targetZone, settings.IncludeApexNs);

                        var changes = settings.IgnoreTtl
                            ? plan.Changes.Where(c => !c.IsTtlOnlyChange).ToList()
                            : plan.Changes.ToList();

                        var hasDrift = changes.Count > 0;
                        if (hasDrift) driftDetected = true;

                        if (jsonMode)
                        {
                            jsonZones.Add(BuildJsonResult(zoneName, targetName, changes));
                        }
                        else if (!silent)
                        {
                            if (hasDrift)
                                AnsiConsole.MarkupLine(
                                    $"  [red]✗[/] {Markup.Escape(zoneName)} → {Markup.Escape(targetName)}" +
                                    $" — [bold]{changes.Count}[/] change(s) " +
                                    $"(+{changes.Count(c => c.ChangeType == ChangeType.Create)} " +
                                    $"~{changes.Count(c => c.ChangeType == ChangeType.Update)} " +
                                    $"-{changes.Count(c => c.ChangeType == ChangeType.Delete)})");
                            else
                                AnsiConsole.MarkupLine(
                                    $"  [green]✓[/] {Markup.Escape(zoneName)} → {Markup.Escape(targetName)} — in sync");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!jsonMode && !silent)
                            AnsiConsole.MarkupLine(
                                $"  [red]✗[/] {Markup.Escape(zoneName)} → {Markup.Escape(targetName)}: {Markup.Escape(ex.Message)}");
                        hasErrors = true;
                    }
                }
            }

            if (jsonMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(jsonZones,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            else if (!silent)
            {
                AnsiConsole.WriteLine();
                if (driftDetected)
                    AnsiConsole.MarkupLine("[red]✗ Drift detected.[/] Run [bold]dns-sync plan[/] to review changes.");
                else if (!hasErrors)
                    AnsiConsole.MarkupLine("[green]✓ All zones are in sync.[/]");
            }

            return hasErrors || driftDetected ? 1 : 0;
        }
        catch (Exception ex)
        {
            CommandHelpers.PrintError(ex, settings.Verbose);
            return 1;
        }
    }

    private static object BuildJsonResult(string zoneName, string targetName, IReadOnlyList<Core.RecordChange> changes) =>
        new
        {
            zone = zoneName,
            target = targetName,
            drift = changes.Count > 0,
            creates = changes.Count(c => c.ChangeType == ChangeType.Create),
            updates = changes.Count(c => c.ChangeType == ChangeType.Update),
            deletes = changes.Count(c => c.ChangeType == ChangeType.Delete),
        };
}
