using Spectre.Console;
using Spectre.Console.Cli;

namespace DnsSync.Commands;

public class CompletionsCommand : Command<CompletionsSettings>
{
    // Keep in sync with the command settings; CompletionsCommandTests fails on drift.
    internal const string BashScript = """
        _dns_sync() {
            local cur="${COMP_WORDS[COMP_CWORD]}"
            local prev="${COMP_WORDS[COMP_CWORD-1]}"
            local base="-c --config --strict -v --verbose --gcp-logs --log-file --env-file -h --help"

            if [[ $COMP_CWORD -eq 1 ]]; then
                COMPREPLY=($(compgen -W "validate plan apply import diff fmt drift completions -h --help --version" -- "$cur"))
                return
            fi

            # Options that take a value: fall back to default (file) completion.
            case "$prev" in
                -c|--config|--log-file|--env-file|--from|--to|-z|--zone|-o|--output|--save-plan|--max-changes|--from-plan|-p|--provider)
                    COMPREPLY=()
                    return
                    ;;
            esac

            local opts
            case "${COMP_WORDS[1]}" in
                validate) opts="$base" ;;
                plan) opts="$base -w --wide --include-apex-ns --exit-code -o --output --save-plan -z --zone" ;;
                apply) opts="$base -w --wide --include-apex-ns --exit-code -o --output --save-plan -z --zone -y --yes --max-changes -f --force --from-plan" ;;
                import) opts="$base -p --provider -z --zone -a --all -o --output -f --force --no-config-update" ;;
                diff) opts="$base --from --to -z --zone --include-apex-ns -w --wide -o --output --exit-code" ;;
                fmt) opts="--check -h --help" ;;
                drift) opts="$base --include-apex-ns --ignore-ttl -o --output -z --zone" ;;
                completions) opts="bash zsh -h --help" ;;
                *) return ;;
            esac

            COMPREPLY=($(compgen -W "$opts" -- "$cur"))
        }
        complete -o default -F _dns_sync dns-sync

        """;

    internal const string ZshScript = """
        (( $+functions[compdef] )) || { autoload -U +X compinit && compinit }
        autoload -U +X bashcompinit && bashcompinit

        """ + BashScript;

    protected override int Execute(CommandContext context, CompletionsSettings settings, CancellationToken cancellationToken)
    {
        var script = settings.Shell switch
        {
            "bash" => BashScript,
            "zsh" => ZshScript,
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
}
