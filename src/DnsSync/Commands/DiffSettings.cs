using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class DiffSettings : BaseSettings
{
    [CommandOption("--from <PROVIDER>")]
    [Description("Source provider name from config (read from)")]
    public string From { get; set; } = string.Empty;

    [CommandOption("--to <PROVIDER>")]
    [Description("Target provider name from config (compare against)")]
    public string To { get; set; } = string.Empty;

    [CommandOption("-z|--zone <ZONE>")]
    [Description("Compare a single zone (e.g. example.com.) — if omitted, discovers all zones from --from provider")]
    public string? Zone { get; set; }

    [CommandOption("--include-apex-ns")]
    [Description("Include apex NS records in the diff (excluded by default to prevent registrar conflicts)")]
    public bool IncludeApexNs { get; set; }

    [CommandOption("-w|--wide")]
    [Description("Show record values on a second line (no truncation)")]
    public bool Wide { get; set; }

    [CommandOption("-o|--output <FORMAT>")]
    [Description("Output format: color (default), plain, diff, or json")]
    [DefaultValue("color")]
    public string Output { get; set; } = "color";

    [CommandOption("--exit-code")]
    [Description("Return exit code 2 when there are differences (Terraform-compatible)")]
    public bool ExitCode { get; set; }
}
