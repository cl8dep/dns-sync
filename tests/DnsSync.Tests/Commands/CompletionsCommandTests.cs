using System.Reflection;
using DnsSync.Commands;
using DnsSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace DnsSync.Tests.Commands;

[Collection("cli-serial")]
public class CompletionsCommandTests : IDisposable
{
    private readonly TestConsole _console = new();
    private readonly IAnsiConsole _previousConsole;

    public CompletionsCommandTests()
    {
        _previousConsole = AnsiConsole.Console;
        AnsiConsole.Console = _console;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _previousConsole;
    }

    private static CommandApp BuildApp()
    {
        var app = new CommandApp(new TypeRegistrar(new ServiceCollection()));
        app.Configure(cfg => cfg.AddCommand<CompletionsCommand>("completions"));
        return app;
    }

    [Fact]
    public void BashScript_ListsEveryRegisteredCommandAndFlag()
    {
        var script = CompletionsCommand.BashScript(Program.Commands);

        foreach (var command in Program.Commands)
        {
            var line = script.Split('\n').Single(l => l.TrimStart().StartsWith(command.Name + ") opts="));
            var opts = line.Split('"')[1].Split(' ');
            var flags = command.Settings.GetProperties()
                .Select(p => p.GetCustomAttribute<CommandOptionAttribute>())
                .OfType<CommandOptionAttribute>()
                .SelectMany(a => a.LongNames.Select(n => "--" + n).Concat(a.ShortNames.Select(n => "-" + n)));
            foreach (var flag in flags)
                opts.ShouldContain(flag, customMessage: $"'{flag}' missing for '{command.Name}'");
        }
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    public async Task SupportedShell_PrintsScript(string shell)
    {
        var exit = await BuildApp().RunAsync(["completions", shell]);

        exit.ShouldBe(0);
        _console.Output.ShouldContain("complete -o default -F _dns_sync dns-sync");
    }

    [Fact]
    public async Task UnsupportedShell_ReturnsExitOne()
    {
        var exit = await BuildApp().RunAsync(["completions", "fish"]);

        exit.ShouldBe(1);
        _console.Output.ShouldContain("Unsupported shell: fish");
    }
}
