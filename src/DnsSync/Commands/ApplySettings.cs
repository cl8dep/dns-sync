using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class ApplySettings : PlanSettings
{
    [CommandOption("--yes|-y")]
    [Description("Skip interactive confirmation prompt (for CI/CD)")]
    public bool Yes { get; set; }

    [CommandOption("--max-changes <N>")]
    [Description("Abort if plan exceeds this number of changes (default: 50). Use --force to override.")]
    [DefaultValue(50)]
    public int MaxChanges { get; set; } = 50;

    [CommandOption("--force")]
    [Description("Override --max-changes safety limit")]
    public bool Force { get; set; }
}
