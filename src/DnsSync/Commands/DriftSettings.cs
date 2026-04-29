using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class DriftSettings : BaseSettings
{
    [CommandOption("--include-apex-ns")]
    [Description("Include apex NS records in the diff (excluded by default to prevent registrar conflicts)")]
    public bool IncludeApexNs { get; set; }

    [CommandOption("--ignore-ttl")]
    [Description("Ignore TTL-only differences when detecting drift")]
    public bool IgnoreTtl { get; set; }

    [CommandOption("-o|--output <FORMAT>")]
    [Description("Output format: color (default), plain, json, or silent")]
    [DefaultValue("color")]
    public string Output { get; set; } = "color";
}
