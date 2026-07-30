using DnsSync.Core;
using DnsSync.Providers.Yaml;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class FmtCommand : Command<FmtSettings>
{
    protected override int Execute(CommandContext context, FmtSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(settings.Directory);

        if (!System.IO.Directory.Exists(directory))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Directory not found: {Markup.Escape(directory)}");
            return 1;
        }

        var files = System.IO.Directory.GetFiles(directory, "*.yaml")
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]~[/] No .yaml files found in {Markup.Escape(directory)}");
            return 0;
        }

        var changed = 0;
        var unchanged = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var zoneName = fileName + ".";
            var original = File.ReadAllText(file);

            IReadOnlyList<DnsRecord> records;
            try
            {
                records = YamlProvider.ParseZoneYaml(original, zoneName);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(fileName)}.yaml, parse error: {Markup.Escape(ex.Message)}");
                continue;
            }

            var zone = new DnsZone { Name = zoneName, Records = records.ToList() };
            var formatted = ZoneYamlSerializer.Serialize(zone);

            if (formatted == original)
            {
                unchanged++;
                continue;
            }

            changed++;

            if (settings.Check)
            {
                AnsiConsole.MarkupLine($"  [yellow]~[/] {Markup.Escape(fileName)}.yaml would be reformatted");
            }
            else
            {
                File.WriteAllText(file, formatted);
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(fileName)}.yaml");
            }
        }

        AnsiConsole.WriteLine();

        if (settings.Check)
        {
            if (changed > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]~[/] {changed} file(s) would be reformatted");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]✓[/] All {unchanged} file(s) already formatted");
            return 0;
        }

        if (changed > 0)
            AnsiConsole.MarkupLine($"[green]✓[/] Reformatted [bold]{changed}[/] file(s)");

        if (unchanged > 0)
            AnsiConsole.MarkupLine($"[dim]{unchanged} file(s) already formatted[/]");

        return 0;
    }
}
