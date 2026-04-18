using DnsSync.Config;
using DnsSync.Core;
using Spectre.Console;

namespace DnsSync.Commands;

public static class CommandHelpers
{
    public static DnsSyncConfig LoadAndValidateConfig(BaseSettings settings)
    {
        AnsiConsole.MarkupLine($"Loading config from [bold]{Markup.Escape(settings.ConfigPath)}[/]");

        DnsSyncConfig config;
        try
        {
            config = ConfigLoader.Load(settings.ConfigPath);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"[red]✗[/] {ex.Message}\n" +
                "  Create a config.yaml or pass --config <path>.");
        }

        var errors = ConfigLoader.ValidateStructure(config);
        if (errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]✗ Config validation failed:[/]");
            foreach (var error in errors)
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(error)}");
            throw new InvalidOperationException("Config is invalid.");
        }

        var zoneCount = config.Zones.Count;
        var providerCount = config.Providers.Count;
        AnsiConsole.MarkupLine(
            $"[green]✓[/] Config valid ([bold]{zoneCount}[/] zone(s), [bold]{providerCount}[/] provider(s))");

        return config;
    }

    public static void PrintPlan(DnsPlan plan, string zoneName, string targetName)
    {
        AnsiConsole.MarkupLine($"\nZone: [bold]{Markup.Escape(zoneName)}[/] → [bold]{Markup.Escape(targetName)}[/]");

        if (plan.IsEmpty)
        {
            AnsiConsole.MarkupLine("  [dim]No changes[/]");
            return;
        }

        foreach (var change in plan.Changes)
        {
            switch (change.ChangeType)
            {
                case ChangeType.Create:
                    AnsiConsole.MarkupLine(
                        $"  [green]+[/] {Markup.Escape(change.RecordName),-45} " +
                        $"[green]{change.RecordType,-6}[/] {change.After!.Ttl,5}  " +
                        $"{Markup.Escape(Truncate(change.After.FormatValues()))}");
                    break;

                case ChangeType.Update when change.IsTtlOnlyChange:
                    AnsiConsole.MarkupLine(
                        $"  [yellow]~[/] {Markup.Escape(change.RecordName),-45} " +
                        $"[yellow]{change.RecordType,-6}[/] " +
                        $"[dim]{change.Before!.Ttl}[/]→[yellow]{change.After!.Ttl}[/]  " +
                        $"{Markup.Escape(Truncate(change.After.FormatValues()))}  [dim](ttl only)[/]");
                    break;

                case ChangeType.Update:
                    AnsiConsole.MarkupLine(
                        $"  [yellow]~[/] {Markup.Escape(change.RecordName),-45} " +
                        $"[yellow]{change.RecordType,-6}[/] {change.After!.Ttl,5}");
                    AnsiConsole.MarkupLine(
                        $"    [dim]before:[/] {Markup.Escape(Truncate(change.Before!.FormatValues(), 120))}");
                    AnsiConsole.MarkupLine(
                        $"    [yellow]after: [/] {Markup.Escape(Truncate(change.After.FormatValues(), 120))}");
                    break;

                case ChangeType.Delete:
                    AnsiConsole.MarkupLine(
                        $"  [red]-[/] {Markup.Escape(change.RecordName),-45} " +
                        $"[red]{change.RecordType,-6}[/] {change.Before!.Ttl,5}  " +
                        $"[dim]{Markup.Escape(Truncate(change.Before.FormatValues()))}[/]");
                    break;
            }
        }

        var summary = new List<string>();
        if (plan.Creates > 0) summary.Add($"[green]{plan.Creates} create(s)[/]");
        if (plan.Updates > 0) summary.Add($"[yellow]{plan.Updates} update(s)[/]");
        if (plan.Deletes > 0) summary.Add($"[red]{plan.Deletes} delete(s)[/]");

        AnsiConsole.MarkupLine($"\n  {string.Join(", ", summary)}");
    }

    public static void PrintError(Exception ex, bool verbose = false)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
        if (verbose && ex.StackTrace is not null)
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.StackTrace)}[/]");
    }

    private static string Truncate(string value, int maxLength = 80)
    {
        if (value.Length <= maxLength) return value;
        return value[..maxLength] + "…";
    }
}
