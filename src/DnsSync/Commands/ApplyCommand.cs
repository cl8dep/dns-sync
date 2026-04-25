using DnsSync.Core;
using DnsSync.Plan;
using DnsSync.Providers;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class ApplyCommand(ILoggerFactory loggerFactory) : AsyncCommand<ApplySettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ApplySettings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.MaxChanges < 1)
            {
                AnsiConsole.MarkupLine("[red]✗ --max-changes must be at least 1.[/]");
                return 1;
            }

            var config = CommandHelpers.LoadAndValidateConfig(settings);

            if (settings.FromPlan is not null)
                return await ApplyFromPlanAsync(settings, config, cancellationToken);


            var useSpinners = !settings.GcpLogs && !settings.Verbose;

            AnsiConsole.MarkupLine("\nRunning pre-flight checks...");

            // Build all plans first, before applying anything
            var plans = new List<(string ZoneName, string TargetName, DnsPlan Plan, IProvider Provider)>();
            var hasErrors = false;

            foreach (var (zoneName, zoneConfig) in config.Zones)
            {
                var sourceProvider = ProviderFactory.Create(
                    zoneConfig.Source, config.Providers[zoneConfig.Source], loggerFactory);

                if (useSpinners)
                    await AnsiConsole.Status().StartAsync($"Checking source '{zoneConfig.Source}'...",
                        async ctx => { ctx.Spinner(Spinner.Known.Dots); await sourceProvider.PreflightAsync(cancellationToken); });
                else
                    await sourceProvider.PreflightAsync(cancellationToken);

                AnsiConsole.MarkupLine($"[green]✓[/] Source '{Markup.Escape(zoneConfig.Source)}' reachable");

                DnsZone sourceZone;
                if (useSpinners)
                    sourceZone = await AnsiConsole.Status().StartAsync($"Fetching {zoneName}...",
                        async ctx => { ctx.Spinner(Spinner.Known.Dots); return await sourceProvider.GetZoneAsync(zoneName, cancellationToken); });
                else
                    sourceZone = await sourceProvider.GetZoneAsync(zoneName, cancellationToken);

                CommandHelpers.ValidateSourceZone(sourceZone, zoneConfig.Source);

                foreach (var targetName in zoneConfig.Targets)
                {
                    var targetProvider = ProviderFactory.Create(
                        targetName, config.Providers[targetName], loggerFactory);

                    try
                    {
                        if (useSpinners)
                            await AnsiConsole.Status().StartAsync($"Checking target '{targetName}'...",
                                async ctx => { ctx.Spinner(Spinner.Known.Dots); await targetProvider.PreflightAsync(cancellationToken); });
                        else
                            await targetProvider.PreflightAsync(cancellationToken);

                        AnsiConsole.MarkupLine($"[green]✓[/] Target '{Markup.Escape(targetName)}' reachable");

                        DnsZone targetZone;
                        if (useSpinners)
                            targetZone = await AnsiConsole.Status().StartAsync($"Fetching {zoneName} from '{targetName}'...",
                                async ctx => { ctx.Spinner(Spinner.Known.Dots); return await targetProvider.GetZoneAsync(zoneName, cancellationToken); });
                        else
                            targetZone = await targetProvider.GetZoneAsync(zoneName, cancellationToken);

                        var plan = ZoneDiff.Diff(sourceZone, targetZone, settings.IncludeApexNs);

                        plans.Add((zoneName, targetName, plan, targetProvider));
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]✗[/] Target '{Markup.Escape(targetName)}': {Markup.Escape(ex.Message)}");
                        hasErrors = true;
                    }
                }
            }

            if (hasErrors)
            {
                AnsiConsole.MarkupLine("\n[red]✗ Pre-flight failed. Aborting.[/]");
                return 1;
            }

            var totalChanges = plans.Sum(p => p.Plan.Total);

            if (totalChanges == 0)
            {
                AnsiConsole.MarkupLine("\n[green]✓ All zones are in sync. Nothing to apply.[/]");
                return 0;
            }

            // Print all plans
            foreach (var (zoneName, targetName, plan, _) in plans)
                CommandHelpers.PrintPlan(plan, zoneName, targetName, settings.Wide);

            // Safety guard: abort if too many changes
            if (!settings.Force && totalChanges > settings.MaxChanges)
            {
                AnsiConsole.MarkupLine(
                    $"\n[red]✗ Plan has {totalChanges} changes, which exceeds --max-changes {settings.MaxChanges}.[/]");
                AnsiConsole.MarkupLine(
                    "  Pass [bold]--force[/] to override this safety limit, or increase [bold]--max-changes[/].");
                return 1;
            }

            // Interactive confirmation
            if (!settings.Yes)
            {
                AnsiConsole.WriteLine();
                var confirmed = AnsiConsole.Confirm(
                    $"Apply [bold]{totalChanges}[/] change(s)?", defaultValue: false);

                if (!confirmed)
                {
                    AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
                    return 0;
                }
            }

            // Apply plans
            AnsiConsole.WriteLine();
            var exitCode = 0;

            foreach (var (zoneName, targetName, plan, provider) in plans)
            {
                if (plan.IsEmpty) continue;

                try
                {
                    AnsiConsole.Markup($"Applying to [bold]{Markup.Escape(targetName)}[/]... ");
                    var result = useSpinners
                        ? await AnsiConsole.Status().StartAsync($"Applying to {targetName}...",
                            async ctx => { ctx.Spinner(Spinner.Known.Dots); return await provider.ApplyPlanAsync(zoneName, plan, cancellationToken); })
                        : await provider.ApplyPlanAsync(zoneName, plan, cancellationToken);

                    if (result.IsSuccess)
                    {
                        AnsiConsole.MarkupLine(
                            $"[green]✓ Applied {result.Applied} change(s)[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]⚠ Applied {result.Applied}, failed {result.Failed}[/]");
                        foreach (var error in result.Errors)
                            AnsiConsole.MarkupLine($"    [red]•[/] {Markup.Escape(error)}");
                        exitCode = 1;
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
                    exitCode = 1;
                }
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            CommandHelpers.PrintError(ex, settings.Verbose);
            return 1;
        }
    }

    private async Task<int> ApplyFromPlanAsync(
        ApplySettings settings,
        Config.DnsSyncConfig config,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"\nLoading plan from [bold]{Markup.Escape(settings.FromPlan!)}[/]...");

        SavedPlanBody savedPlan;
        try
        {
            savedPlan = PlanFileSerializer.Load(settings.ConfigPath, settings.FromPlan!);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]✓[/] Plan signature verified.");

        var plans = new List<(string ZoneName, string TargetName, DnsPlan Plan, IProvider Provider)>();

        foreach (var savedZone in savedPlan.Zones)
        {
            if (!config.Providers.TryGetValue(savedZone.Target, out var providerConfig))
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗ Plan references unknown provider '{Markup.Escape(savedZone.Target)}'.[/]");
                return 1;
            }

            var provider = ProviderFactory.Create(savedZone.Target, providerConfig, loggerFactory);
            var plan = PlanFileSerializer.ToDnsPlan(savedZone);
            plans.Add((savedZone.Zone, savedZone.Target, plan, provider));
        }

        var totalChanges = plans.Sum(p => p.Plan.Total);

        if (totalChanges == 0)
        {
            AnsiConsole.MarkupLine("\n[green]✓ Plan has no changes. Nothing to apply.[/]");
            return 0;
        }

        foreach (var (zoneName, targetName, plan, _) in plans)
            CommandHelpers.PrintPlan(plan, zoneName, targetName, settings.Wide);

        if (!settings.Force && totalChanges > settings.MaxChanges)
        {
            AnsiConsole.MarkupLine(
                $"\n[red]✗ Plan has {totalChanges} changes, which exceeds --max-changes {settings.MaxChanges}.[/]");
            AnsiConsole.MarkupLine(
                "  Pass [bold]--force[/] to override this safety limit, or increase [bold]--max-changes[/].");
            return 1;
        }

        if (!settings.Yes)
        {
            AnsiConsole.WriteLine();
            var confirmed = AnsiConsole.Confirm(
                $"Apply [bold]{totalChanges}[/] change(s) from saved plan?", defaultValue: false);

            if (!confirmed)
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
                return 0;
            }
        }

        AnsiConsole.WriteLine();
        var useSpinners = !settings.GcpLogs && !settings.Verbose;
        var exitCode = 0;

        foreach (var (zoneName, targetName, plan, provider) in plans)
        {
            if (plan.IsEmpty) continue;

            try
            {
                AnsiConsole.Markup($"Applying to [bold]{Markup.Escape(targetName)}[/]... ");
                var result = useSpinners
                    ? await AnsiConsole.Status().StartAsync($"Applying to {targetName}...",
                        async ctx => { ctx.Spinner(Spinner.Known.Dots); return await provider.ApplyPlanAsync(zoneName, plan, cancellationToken); })
                    : await provider.ApplyPlanAsync(zoneName, plan, cancellationToken);

                if (result.IsSuccess)
                    AnsiConsole.MarkupLine($"[green]✓ Applied {result.Applied} change(s)[/]");
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ Applied {result.Applied}, failed {result.Failed}[/]");
                    foreach (var error in result.Errors)
                        AnsiConsole.MarkupLine($"    [red]•[/] {Markup.Escape(error)}");
                    exitCode = 1;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
                exitCode = 1;
            }
        }

        return exitCode;
    }
}
