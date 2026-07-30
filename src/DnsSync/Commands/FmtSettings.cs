using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class FmtSettings : CommandSettings
{
    [CommandArgument(0, "[DIRECTORY]")]
    [Description("Directory containing zone YAML files (default: ./zones)")]
    [DefaultValue("./zones")]
    public string Directory { get; set; } = "./zones";

    [CommandOption("--check")]
    [Description("Check formatting without writing changes (exit 1 if files would change)")]
    public bool Check { get; set; }
}
