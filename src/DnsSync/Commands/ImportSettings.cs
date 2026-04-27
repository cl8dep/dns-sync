using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class ImportSettings : BaseSettings
{
    [CommandOption("-p|--provider <NAME>")]
    [Description("Provider name from config to import from")]
    public string Provider { get; set; } = string.Empty;

    [CommandOption("-z|--zone <ZONE>")]
    [Description("Import a single zone (e.g. example.com.)")]
    public string? Zone { get; set; }

    [CommandOption("--all")]
    [Description("Import all zones from the provider")]
    public bool All { get; set; }

    [CommandOption("-o|--output <DIR>")]
    [Description("Directory to write zone YAML files (default: ./zones)")]
    [DefaultValue("./zones")]
    public string Output { get; set; } = "./zones";

    [CommandOption("--force")]
    [Description("Overwrite existing zone files")]
    public bool Force { get; set; }
}
