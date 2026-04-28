using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class BaseSettings : CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    [Description("Path to the YAML configuration file")]
    [DefaultValue("config.yaml")]
    public string ConfigPath { get; set; } = "config.yaml";

    [CommandOption("--strict")]
    [Description("Treat validation warnings as errors")]
    public bool Strict { get; set; }

    [CommandOption("--verbose|-v")]
    [Description("Enable debug log output")]
    public bool Verbose { get; set; }

    [CommandOption("--gcp-logs")]
    [Description("Output structured JSON logs for Google Cloud Logging (auto-enabled in Cloud Run)")]
    public bool GcpLogs { get; set; }

    [CommandOption("--log-file <PATH>")]
    [Description("Also write logs to a file at the specified path")]
    public string? LogFile { get; set; }

    [CommandOption("--env-file <PATH>")]
    [Description("Load environment variables from a .env file (default: .env in current directory)")]
    public string? EnvFile { get; set; }
}
