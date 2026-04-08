using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class PlanSettings : BaseSettings
{
    [CommandOption("--include-apex-ns")]
    [Description("Include apex NS records in the diff (excluded by default to prevent registrar conflicts)")]
    public bool IncludeApexNs { get; set; }

    [CommandOption("--exit-code")]
    [Description("Return exit code 2 when there are pending changes (Terraform-compatible)")]
    public bool ExitCode { get; set; }

    [CommandOption("--dry-run")]
    [Description("Alias for plan — show changes without applying (cosmetic)")]
    public bool DryRun { get; set; }

    [CommandOption("--output <FORMAT>")]
    [Description("Output format: text (default) or json")]
    [DefaultValue("text")]
    public string Output { get; set; } = "text";
}
