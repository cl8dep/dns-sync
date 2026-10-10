using System.ComponentModel;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class CompletionsSettings : CommandSettings
{
    [CommandArgument(0, "<SHELL>")]
    [Description("Shell to generate the completion script for: bash or zsh")]
    public string Shell { get; set; } = "";
}
