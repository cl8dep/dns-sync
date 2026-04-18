using DnsSync.Providers;
using DnsSync.Providers.Yaml;
using DnsSync.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class ValidateCommand : AsyncCommand<BaseSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, BaseSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var config = CommandHelpers.LoadAndValidateConfig(settings);

            // Validate each zone file via YamlProvider
            var hasErrors = false;
            foreach (var (zoneName, zoneConfig) in config.Zones)
            {
                var sourceProvider = config.Providers[zoneConfig.Source];
                if (!string.Equals(sourceProvider.Type, "yaml", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var provider = new YamlProvider(sourceProvider.Directory!);
                    await provider.PreflightAsync(cancellationToken);
                    var zone = await provider.GetZoneAsync(zoneName, cancellationToken);
                    var result = ZoneValidator.Validate(zone);

                    if (result.Errors.Count > 0 || result.Warnings.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"\nZone [bold]{Markup.Escape(zoneName)}[/]:");
                        foreach (var w in result.Warnings)
                            AnsiConsole.MarkupLine($"  [yellow]⚠[/] {Markup.Escape(w)}");
                        foreach (var e in result.Errors)
                            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(e)}");
                    }

                    if (!result.IsValid)
                        hasErrors = true;
                    else if (settings.Strict && !result.IsValidStrict)
                        hasErrors = true;
                    else
                        AnsiConsole.MarkupLine($"[green]✓[/] Zone {Markup.Escape(zoneName)} is valid ({zone.Records.Count} records)");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] Zone {Markup.Escape(zoneName)}: {Markup.Escape(ex.Message)}");
                    hasErrors = true;
                }
            }

            return hasErrors ? 1 : 0;
        }
        catch (Exception ex)
        {
            CommandHelpers.PrintError(ex, settings.Verbose);
            return 1;
        }
    }
}
