using DnsSync.Config;
using DnsSync.Core;
using DnsSync.Validation;
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
        var groupCount = config.ZoneGroups.Count;
        var providerCount = config.Providers.Count;
        var groupPart = groupCount > 0 ? $", [bold]{groupCount}[/] zone group(s)" : "";
        AnsiConsole.MarkupLine(
            $"[green]✓[/] Config valid ([bold]{zoneCount}[/] zone(s){groupPart}, [bold]{providerCount}[/] provider(s))");

        return config;
    }

    /// <summary>
    /// Validates a source zone's records and throws if any errors are found.
    /// Warnings are printed but do not abort.
    /// </summary>
    public static void ValidateSourceZone(DnsZone zone, string sourceName)
    {
        var result = ZoneValidator.Validate(zone);
        foreach (var w in result.Warnings)
            AnsiConsole.MarkupLine($"  [yellow]⚠[/] {Markup.Escape(w)}");
        if (!result.IsValid)
        {
            foreach (var e in result.Errors)
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(e)}");
            throw new InvalidOperationException(
                $"Zone '{zone.Name}' from source '{sourceName}' has {result.Errors.Count} validation error(s). Fix the zone file and retry.");
        }
    }

    public static void PrintPlan(DnsPlan plan, string zoneName, string targetName, bool wide = false, string output = "color")
    {
        if (string.Equals(output, "diff", StringComparison.OrdinalIgnoreCase))
        {
            PrintPlanDiff(plan, zoneName, targetName, wide);
            return;
        }

        if (string.Equals(output, "plain", StringComparison.OrdinalIgnoreCase))
        {
            PrintPlanPlain(plan, zoneName, targetName, wide);
            return;
        }

        // color (default)
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
                        $"[green]{change.RecordType,-6}[/] {change.After!.Ttl,5}" +
                        (wide ? "" : $"  {Markup.Escape(Truncate(change.After.FormatValues()))}"));
                    if (wide)
                        AnsiConsole.MarkupLine($"      [green]{Markup.Escape(change.After.FormatValues())}[/]");
                    break;

                case ChangeType.Update when change.IsTtlOnlyChange:
                    AnsiConsole.MarkupLine(
                        $"  [yellow]~[/] {Markup.Escape(change.RecordName),-45} " +
                        $"[yellow]{change.RecordType,-6}[/] " +
                        $"[dim]{change.Before!.Ttl}[/]→[yellow]{change.After!.Ttl}[/]" +
                        (wide ? "  [dim](ttl only)[/]" : $"  {Markup.Escape(Truncate(change.After.FormatValues()))}  [dim](ttl only)[/]"));
                    if (wide)
                        AnsiConsole.MarkupLine($"      {Markup.Escape(change.After!.FormatValues())}");
                    break;

                case ChangeType.Update:
                    AnsiConsole.MarkupLine(
                        $"  [yellow]~[/] {Markup.Escape(change.RecordName),-45} " +
                        $"[yellow]{change.RecordType,-6}[/] {change.After!.Ttl,5}");
                    if (wide)
                    {
                        AnsiConsole.MarkupLine($"      [dim]before:[/] {Markup.Escape(change.Before!.FormatValues())}");
                        AnsiConsole.MarkupLine($"      [yellow]after: [/] {Markup.Escape(change.After.FormatValues())}");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"    [dim]before:[/] {Markup.Escape(Truncate(change.Before!.FormatValues(), 120))}");
                        AnsiConsole.MarkupLine($"    [yellow]after: [/] {Markup.Escape(Truncate(change.After.FormatValues(), 120))}");
                    }
                    break;

                case ChangeType.Delete:
                    AnsiConsole.MarkupLine(
                        $"  [red]-[/] {Markup.Escape(change.RecordName),-45} " +
                        $"[red]{change.RecordType,-6}[/] {change.Before!.Ttl,5}" +
                        (wide ? "" : $"  [dim]{Markup.Escape(Truncate(change.Before.FormatValues()))}[/]"));
                    if (wide)
                        AnsiConsole.MarkupLine($"      [dim]{Markup.Escape(change.Before.FormatValues())}[/]");
                    break;
            }
        }

        var summary = new List<string>();
        if (plan.Creates > 0) summary.Add($"[green]{plan.Creates} create(s)[/]");
        if (plan.Updates > 0) summary.Add($"[yellow]{plan.Updates} update(s)[/]");
        if (plan.Deletes > 0) summary.Add($"[red]{plan.Deletes} delete(s)[/]");

        AnsiConsole.MarkupLine($"\n  {string.Join(", ", summary)}");
    }

    private static void PrintPlanPlain(DnsPlan plan, string zoneName, string targetName, bool wide)
    {
        Console.WriteLine($"\nZone: {zoneName} → {targetName}");

        if (plan.IsEmpty)
        {
            Console.WriteLine("  No changes");
            return;
        }

        foreach (var change in plan.Changes)
        {
            switch (change.ChangeType)
            {
                case ChangeType.Create:
                    Console.WriteLine($"  + {change.RecordName,-45} {change.RecordType,-6} {change.After!.Ttl,5}" +
                        (wide ? "" : $"  {Truncate(change.After.FormatValues())}"));
                    if (wide)
                        Console.WriteLine($"      {change.After.FormatValues()}");
                    break;

                case ChangeType.Update when change.IsTtlOnlyChange:
                    Console.WriteLine($"  ~ {change.RecordName,-45} {change.RecordType,-6} {change.Before!.Ttl}→{change.After!.Ttl}" +
                        (wide ? "  (ttl only)" : $"  {Truncate(change.After.FormatValues())}  (ttl only)"));
                    if (wide)
                        Console.WriteLine($"      {change.After!.FormatValues()}");
                    break;

                case ChangeType.Update:
                    Console.WriteLine($"  ~ {change.RecordName,-45} {change.RecordType,-6} {change.After!.Ttl,5}");
                    if (wide)
                    {
                        Console.WriteLine($"      before: {change.Before!.FormatValues()}");
                        Console.WriteLine($"      after:  {change.After.FormatValues()}");
                    }
                    else
                    {
                        Console.WriteLine($"    before: {Truncate(change.Before!.FormatValues(), 120)}");
                        Console.WriteLine($"    after:  {Truncate(change.After.FormatValues(), 120)}");
                    }
                    break;

                case ChangeType.Delete:
                    Console.WriteLine($"  - {change.RecordName,-45} {change.RecordType,-6} {change.Before!.Ttl,5}" +
                        (wide ? "" : $"  {Truncate(change.Before.FormatValues())}"));
                    if (wide)
                        Console.WriteLine($"      {change.Before.FormatValues()}");
                    break;
            }
        }

        var summary = new List<string>();
        if (plan.Creates > 0) summary.Add($"{plan.Creates} create(s)");
        if (plan.Updates > 0) summary.Add($"{plan.Updates} update(s)");
        if (plan.Deletes > 0) summary.Add($"{plan.Deletes} delete(s)");
        Console.WriteLine($"\n  {string.Join(", ", summary)}");
    }

    private static void PrintPlanDiff(DnsPlan plan, string zoneName, string targetName, bool wide)
    {
        Console.WriteLine($"\n# Zone: {zoneName} → {targetName}");

        if (plan.IsEmpty)
        {
            Console.WriteLine("# No changes");
            return;
        }

        foreach (var change in plan.Changes)
        {
            switch (change.ChangeType)
            {
                case ChangeType.Create:
                    Console.WriteLine($"+ {change.RecordName,-45} {change.RecordType,-6} {change.After!.Ttl,5}" +
                        (wide ? "" : $"  {Truncate(change.After.FormatValues())}"));
                    if (wide)
                        Console.WriteLine($"+   {change.After.FormatValues()}");
                    break;

                case ChangeType.Update when change.IsTtlOnlyChange:
                    Console.WriteLine($"- {change.RecordName,-45} {change.RecordType,-6} {change.Before!.Ttl,5}  (ttl only)");
                    Console.WriteLine($"+ {change.RecordName,-45} {change.RecordType,-6} {change.After!.Ttl,5}  (ttl only)");
                    break;

                case ChangeType.Update:
                    Console.WriteLine($"- {change.RecordName,-45} {change.RecordType,-6} {change.Before!.Ttl,5}");
                    if (wide)
                        Console.WriteLine($"-   {change.Before.FormatValues()}");
                    else
                        Console.WriteLine($"-   {Truncate(change.Before.FormatValues(), 120)}");
                    Console.WriteLine($"+ {change.RecordName,-45} {change.RecordType,-6} {change.After!.Ttl,5}");
                    if (wide)
                        Console.WriteLine($"+   {change.After.FormatValues()}");
                    else
                        Console.WriteLine($"+   {Truncate(change.After.FormatValues(), 120)}");
                    break;

                case ChangeType.Delete:
                    Console.WriteLine($"- {change.RecordName,-45} {change.RecordType,-6} {change.Before!.Ttl,5}" +
                        (wide ? "" : $"  {Truncate(change.Before.FormatValues())}"));
                    if (wide)
                        Console.WriteLine($"-   {change.Before.FormatValues()}");
                    break;
            }
        }

        var parts = new List<string>();
        if (plan.Creates > 0) parts.Add($"{plan.Creates} create(s)");
        if (plan.Updates > 0) parts.Add($"{plan.Updates} update(s)");
        if (plan.Deletes > 0) parts.Add($"{plan.Deletes} delete(s)");
        Console.WriteLine($"\n# {string.Join(", ", parts)}");
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
