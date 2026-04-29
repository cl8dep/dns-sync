using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class PlanSettings : BaseSettings
{
    [CommandOption("-w|--wide")]
    [Description("Show record values on a second line (no truncation)")]
    public bool Wide { get; set; }

    [CommandOption("--include-apex-ns")]
    [Description("Include apex NS records in the diff (excluded by default to prevent registrar conflicts)")]
    public bool IncludeApexNs { get; set; }

    [CommandOption("--exit-code")]
    [Description("Return exit code 2 when there are pending changes (Terraform-compatible)")]
    public bool ExitCode { get; set; }

    [CommandOption("-o|--output <FORMAT>")]
    [Description("Output format: color (default), plain, diff, or json")]
    [DefaultValue("color")]
    public string Output { get; set; } = "color";

    [CommandOption("--save-plan <PATH>")]
    [Description("Save the computed plan to a signed YAML file for later use with 'apply --from-plan'")]
    public string? SavePlan { get; set; }

    [CommandOption("-z|--zone <ZONE>")]
    [Description("Only process a single zone (e.g. example.com.) — skips preflight for all other providers")]
    public string? Zone { get; set; }
}
