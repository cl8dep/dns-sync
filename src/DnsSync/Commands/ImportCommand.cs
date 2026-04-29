using DnsSync.Providers;
using DnsSync.Providers.Yaml;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class ImportCommand(ILoggerFactory loggerFactory, IProviderFactory providerFactory) : AsyncCommand<ImportSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, ImportSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var config = CommandHelpers.LoadAndValidateConfig(settings);

            if (string.IsNullOrWhiteSpace(settings.Provider))
            {
                AnsiConsole.MarkupLine("[red]✗[/] --provider is required.");
                return 1;
            }

            if (!config.Providers.TryGetValue(settings.Provider, out var providerConfig))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Provider '{Markup.Escape(settings.Provider)}' not found in config.");
                return 1;
            }

            if (!settings.All && string.IsNullOrWhiteSpace(settings.Zone))
            {
                AnsiConsole.MarkupLine("[red]✗[/] Specify --zone <ZONE> or --all.");
                return 1;
            }

            var provider = providerFactory.Create(settings.Provider, providerConfig, loggerFactory);

            AnsiConsole.MarkupLine($"\nRunning pre-flight for [bold]{Markup.Escape(settings.Provider)}[/]...");
            await provider.PreflightAsync(cancellationToken);
            AnsiConsole.MarkupLine($"[green]✓[/] Provider reachable");

            var outputDir = Path.GetFullPath(settings.Output);
            Directory.CreateDirectory(outputDir);

            IReadOnlyList<string> zones;
            if (settings.All)
            {
                zones = await AnsiConsole.Status().StartAsync(
                    "Fetching zone list...",
                    async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        return await provider.GetZonesAsync(cancellationToken);
                    });

                AnsiConsole.MarkupLine($"[green]✓[/] Found [bold]{zones.Count}[/] zone(s)");
            }
            else
            {
                zones = [settings.Zone!];
            }

            var imported = 0;
            var skipped = 0;
            var importedZones = new List<string>();

            foreach (var zoneName in zones)
            {
                var outputPath = Path.Combine(outputDir, zoneName.TrimEnd('.') + ".yaml");

                if (File.Exists(outputPath) && !settings.Force)
                {
                    AnsiConsole.MarkupLine(
                        $"  [yellow]~[/] {Markup.Escape(zoneName)} — skipped (file exists, use --force to overwrite)");
                    skipped++;
                    continue;
                }

                var zone = await AnsiConsole.Status().StartAsync(
                    $"Fetching {zoneName}...",
                    async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        return await provider.GetZoneAsync(zoneName, cancellationToken);
                    });

                var yaml = ZoneYamlSerializer.Serialize(zone, settings.Provider);
                await File.WriteAllTextAsync(outputPath, yaml, cancellationToken);

                AnsiConsole.MarkupLine(
                    $"  [green]+[/] {Markup.Escape(zoneName)} → [dim]{Markup.Escape(outputPath)}[/] " +
                    $"([bold]{zone.Records.Count}[/] record(s))");
                imported++;
                importedZones.Add(zoneName);
            }

            if (!settings.NoConfigUpdate && importedZones.Count > 0)
            {
                var configPath = Path.GetFullPath(settings.ConfigPath);
                var configText = await File.ReadAllTextAsync(configPath, cancellationToken);
                var added = 0;
                var alreadyInConfig = 0;
                var appendBuilder = new System.Text.StringBuilder();

                foreach (var zoneName in importedZones)
                {
                    if (config.Zones.ContainsKey(zoneName))
                    {
                        alreadyInConfig++;
                        continue;
                    }

                    appendBuilder.AppendLine($"  {zoneName}:");
                    appendBuilder.AppendLine($"    source: {settings.Provider}");
                    appendBuilder.AppendLine($"    targets: []");
                    added++;
                }

                if (added > 0)
                {
                    configText = configText.TrimEnd() + "\n" + appendBuilder.ToString().TrimEnd() + "\n";
                    await File.WriteAllTextAsync(configPath, configText, cancellationToken);
                }

                AnsiConsole.WriteLine();
                if (added > 0)
                    AnsiConsole.MarkupLine($"[green]✓[/] Added [bold]{added}[/] zone(s) to config");
                if (alreadyInConfig > 0)
                    AnsiConsole.MarkupLine($"[yellow]~[/] Skipped [bold]{alreadyInConfig}[/] zone(s) (already in config)");
            }

            AnsiConsole.WriteLine();

            if (imported > 0)
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] Imported [bold]{imported}[/] zone(s) to [bold]{Markup.Escape(outputDir)}[/]");

            if (skipped > 0)
                AnsiConsole.MarkupLine($"[yellow]~[/] Skipped [bold]{skipped}[/] zone(s) (already exist)");

            if (imported > 0)
                AnsiConsole.MarkupLine(
                    $"\nRun [bold]dns-sync validate[/] to verify the imported zones.");

            return 0;
        }
        catch (Exception ex)
        {
            CommandHelpers.PrintError(ex, settings.Verbose);
            return 1;
        }
    }
}
