using System.Reflection;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class CompletionsCommand : Command<CompletionsSettings>
{
    private const string ZshPrelude = """
        (( $+functions[compdef] )) || { autoload -U +X compinit && compinit }
        autoload -U +X bashcompinit && bashcompinit

        """;

    protected override int Execute(CommandContext context, CompletionsSettings settings, CancellationToken cancellationToken)
    {
        var script = settings.Shell switch
        {
            "bash" => BashScript(Program.Commands),
            "zsh" => ZshPrelude + BashScript(Program.Commands),
            _ => null,
        };

        if (script is null)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Unsupported shell: {Markup.Escape(settings.Shell)}. Supported shells: bash, zsh");
            return 1;
        }

        // Raw write: markup rendering would mangle brackets and wrap long lines.
        AnsiConsole.Profile.Out.Writer.Write(script);
        return 0;
    }

    internal static string BashScript(IEnumerable<CliCommand> commands)
    {
        var cases = new StringBuilder();
        foreach (var command in commands)
        {
            var options = command.Settings.GetProperties()
                .Select(p => (Property: p, Attribute: p.GetCustomAttribute<CommandOptionAttribute>()))
                .Where(o => o.Attribute is not null)
                .ToList();
            var flags = options.SelectMany(o => OptionNames(o.Attribute!)).Append("-h").Append("--help");
            // Options declared with a <VALUE> and a non-bool property take an argument.
            var valueFlags = options
                .Where(o => o.Attribute!.ValueName is not null && o.Property.PropertyType != typeof(bool))
                .SelectMany(o => OptionNames(o.Attribute!));

            cases.AppendLine($"        {command.Name}) opts=\"{string.Join(' ', flags)}\"; values=\"{string.Join(' ', valueFlags)}\" ;;");
        }

        var names = string.Join(' ', commands.Select(c => c.Name));

        return $$"""
            _dns_sync() {
                local cur="${COMP_WORDS[COMP_CWORD]}"
                local prev="${COMP_WORDS[COMP_CWORD-1]}"
                COMPREPLY=()

                if [[ $COMP_CWORD -eq 1 ]]; then
                    COMPREPLY=($(compgen -W "{{names}} -h --help --version" -- "$cur"))
                    return
                fi

                local opts values
                case "${COMP_WORDS[1]}" in
            {{cases.ToString().TrimEnd()}}
                    *) return ;;
                esac

                # After an option that takes a value, fall back to default (file) completion.
                [[ " $values " == *" $prev "* ]] && return

                COMPREPLY=($(compgen -W "$opts" -- "$cur"))
            }
            complete -o default -F _dns_sync dns-sync

            """;
    }

    private static IEnumerable<string> OptionNames(CommandOptionAttribute attribute)
    {
        return attribute.ShortNames.Select(n => "-" + n).Concat(attribute.LongNames.Select(n => "--" + n));
    }
}
