using System.Text.Json;
using DnsSync.Core;
using DnsSync.Providers;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class DiffCommand(ILoggerFactory loggerFactory) : AsyncCommand<DiffSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DiffSettings settings, CancellationToken cancellationToken)
    {
        var output = settings.Output.ToLowerInvariant();
        if (output is not ("color" or "plain" or "diff" or "json"))
            throw new InvalidOperationException($"Unknown --output value '{settings.Output}'. Valid values: color, plain, diff, json.");
        var jsonMode = output == "json";
        var useSpinners = !jsonMode && !settings.GcpLogs && !settings.Verbose;

        try
        {
            var config = CommandHelpers.LoadAndValidateConfig(settings);

            if (string.IsNullOrWhiteSpace(settings.From))
            {
                AnsiConsole.MarkupLine("[red]✗[/] --from is required.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(settings.To))
            {
                AnsiConsole.MarkupLine("[red]✗[/] --to is required.");
                return 1;
            }

            if (!config.Providers.TryGetValue(settings.From, out var fromConfig))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Provider '{Markup.Escape(settings.From)}' not found in config.");
                return 1;
            }

            if (!config.Providers.TryGetValue(settings.To, out var toConfig))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Provider '{Markup.Escape(settings.To)}' not found in config.");
                return 1;
            }

            if (string.Equals(settings.From, settings.To, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[red]✗[/] --from and --to must be different providers.");
                return 1;
            }

            var fromProvider = ProviderFactory.Create(settings.From, fromConfig, loggerFactory);
            var toProvider = ProviderFactory.Create(settings.To, toConfig, loggerFactory);

            if (!jsonMode)
                AnsiConsole.MarkupLine("\nRunning pre-flight checks...");

            if (useSpinners)
            {
                await AnsiConsole.Status().StartAsync(
                    $"Checking '{settings.From}'...",
                    async ctx => { ctx.Spinner(Spinner.Known.Dots); await fromProvider.PreflightAsync(cancellationToken); });
                await AnsiConsole.Status().StartAsync(
                    $"Checking '{settings.To}'...",
                    async ctx => { ctx.Spinner(Spinner.Known.Dots); await toProvider.PreflightAsync(cancellationToken); });
            }
            else
            {
                await fromProvider.PreflightAsync(cancellationToken);
                await toProvider.PreflightAsync(cancellationToken);
            }

            if (!jsonMode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] '{Markup.Escape(settings.From)}' reachable");
                AnsiConsole.MarkupLine($"[green]✓[/] '{Markup.Escape(settings.To)}' reachable");
            }

            // Determine which zones to compare
            IReadOnlyList<string> zones;
            if (!string.IsNullOrEmpty(settings.Zone))
            {
                zones = [settings.Zone];
            }
            else
            {
                if (useSpinners)
                    zones = await AnsiConsole.Status().StartAsync(
                        $"Discovering zones from '{settings.From}'...",
                        async ctx => { ctx.Spinner(Spinner.Known.Dots); return await fromProvider.GetZonesAsync(cancellationToken); });
                else
                    zones = await fromProvider.GetZonesAsync(cancellationToken);

                if (!jsonMode)
                    AnsiConsole.MarkupLine($"[green]✓[/] Found [bold]{zones.Count}[/] zone(s) in '{Markup.Escape(settings.From)}'");
            }

            var totalChanges = 0;
            var hasErrors = false;
            var jsonZones = new List<object>();

            foreach (var zoneName in zones)
            {
                try
                {
                    DnsZone fromZone;
                    if (useSpinners)
                        fromZone = await AnsiConsole.Status().StartAsync(
                            $"Fetching {zoneName} from '{settings.From}'...",
                            async ctx => { ctx.Spinner(Spinner.Known.Dots); return await fromProvider.GetZoneAsync(zoneName, cancellationToken); });
                    else
                        fromZone = await fromProvider.GetZoneAsync(zoneName, cancellationToken);

                    DnsZone toZone;
                    try
                    {
                        if (useSpinners)
                            toZone = await AnsiConsole.Status().StartAsync(
                                $"Fetching {zoneName} from '{settings.To}'...",
                                async ctx => { ctx.Spinner(Spinner.Known.Dots); return await toProvider.GetZoneAsync(zoneName, cancellationToken); });
                        else
                            toZone = await toProvider.GetZoneAsync(zoneName, cancellationToken);
                    }
                    catch
                    {
                        // Zone doesn't exist on target — treat as empty (all records = creates)
                        toZone = new DnsZone { Name = zoneName, Records = [] };
                        if (!jsonMode)
                            AnsiConsole.MarkupLine(
                                $"  [yellow]~[/] Zone '{Markup.Escape(zoneName)}' not found in '{Markup.Escape(settings.To)}' — treating as empty");
                    }

                    var plan = ZoneDiff.Diff(fromZone, toZone, settings.IncludeApexNs);

                    if (jsonMode)
                        jsonZones.Add(BuildJsonDiff(zoneName, settings.From, settings.To, plan));
                    else
                        CommandHelpers.PrintPlan(plan, zoneName, settings.To, settings.Wide, output);

                    totalChanges += plan.Total;
                }
                catch (Exception ex)
                {
                    if (!jsonMode)
                        AnsiConsole.MarkupLine(
                            $"[red]✗[/] Zone '{Markup.Escape(zoneName)}': {Markup.Escape(ex.Message)}");
                    hasErrors = true;
                }
            }

            if (jsonMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(jsonZones, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                if (totalChanges > 0)
                    AnsiConsole.MarkupLine(
                        $"\n[bold]{totalChanges} difference(s) found[/] between '{Markup.Escape(settings.From)}' and '{Markup.Escape(settings.To)}'.");
                else if (!hasErrors)
                    AnsiConsole.MarkupLine(
                        $"\n[green]✓ Providers '{Markup.Escape(settings.From)}' and '{Markup.Escape(settings.To)}' are in sync.[/]");
            }

            return hasErrors ? 1 : (settings.ExitCode && totalChanges > 0 ? 2 : 0);
        }
        catch (Exception ex)
        {
            CommandHelpers.PrintError(ex, settings.Verbose);
            return 1;
        }
    }

    private static object BuildJsonDiff(string zoneName, string fromName, string toName, DnsPlan plan)
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
            from = fromName,
            to = toName,
            creates = plan.Creates,
            updates = plan.Updates,
            deletes = plan.Deletes,
            changes
        };
    }
}
