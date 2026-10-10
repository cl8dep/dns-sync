using Spectre.Console.Cli;

namespace DnsSync.Commands;

/// <summary>
/// A top-level CLI command: registers itself with Spectre and exposes its settings
/// type so the completion script can be generated from the same list.
/// </summary>
public sealed record CliCommand(string Name, Type Settings, Action<IConfigurator> Register)
{
    public static CliCommand Create<TCommand, TSettings>(string name, Action<ICommandConfigurator> configure)
        where TCommand : class, ICommand<TSettings>
        where TSettings : CommandSettings
    {
        return new CliCommand(name, typeof(TSettings), c => configure(c.AddCommand<TCommand>(name)));
    }
}
