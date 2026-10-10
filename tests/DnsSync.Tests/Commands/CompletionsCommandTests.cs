using System.Reflection;
using System.Text.RegularExpressions;
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

    // Every concrete command class, named by convention (PlanCommand -> plan), with its option names.
    private static Dictionary<string, HashSet<string>> CliCommands()
    {
        return typeof(CompletionsCommand).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ICommand).IsAssignableFrom(t))
            .ToDictionary(
                t => t.Name[..^"Command".Length].ToLowerInvariant(),
                t =>
                {
                    var settings = t.BaseType!.GetGenericArguments()[0];
                    var flags = settings.GetProperties()
                        .Select(p => p.GetCustomAttribute<CommandOptionAttribute>())
                        .OfType<CommandOptionAttribute>()
                        .SelectMany(a => a.LongNames.Select(n => "--" + n).Concat(a.ShortNames.Select(n => "-" + n)))
                        .ToHashSet();
                    flags.UnionWith(["-h", "--help"]);
                    return flags;
                });
    }

    [Fact]
    public void BashScript_CoversEveryCommandAndFlag()
    {
        var script = CompletionsCommand.BashScript;
        var baseFlags = Regex.Match(script, @"local base=""([^""]*)""").Groups[1].Value;
        var scriptCommands = Regex.Matches(script, @"^\s*(\w+)\) opts=""([^""]*)""", RegexOptions.Multiline)
            .ToDictionary(
                m => m.Groups[1].Value,
                m => m.Groups[2].Value.Replace("$base", baseFlags)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.StartsWith('-'))
                    .ToHashSet());
        var rootWords = Regex.Match(script, @"compgen -W ""(validate[^""]*)""").Groups[1].Value.Split(' ');

        var cli = CliCommands();

        cli.Keys.Count.ShouldBe(8);
        scriptCommands.Keys.ShouldBe(cli.Keys, ignoreOrder: true);
        rootWords.ShouldBeSubsetOf(cli.Keys.Concat(["-h", "--help", "--version"]));
        cli.Keys.ShouldBeSubsetOf(rootWords);
        foreach (var (name, flags) in cli)
            scriptCommands[name].ShouldBe(flags, ignoreOrder: true, customMessage: $"flags for '{name}'");
    }

    [Fact]
    public void ZshScript_WrapsBashScript()
    {
        CompletionsCommand.ZshScript.ShouldContain("bashcompinit");
        CompletionsCommand.ZshScript.ShouldEndWith(CompletionsCommand.BashScript);
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
