using System.Reflection;
using DnsSync.Commands;
using DnsSync.Config;
using DnsSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Help;

// Load .env file early — before config interpolation and logging setup.
// Buffer log messages and emit them after Serilog is configured.
// Explicit: --env-file <path>  → load that file (error if not found)
// Implicit: no flag            → silently try .env in current directory
var envFileArg = args.Contains("--env-file")
    ? args.SkipWhile(a => a != "--env-file").Skip(1).FirstOrDefault()
    : null;

string? dotEnvInfo = null;
string? dotEnvDebug = null;

if (envFileArg is not null)
{
    if (!File.Exists(envFileArg))
    {
        Console.Error.WriteLine($"✗ .env file not found: {envFileArg}");
        return 1;
    }
    var loaded = DotEnvLoader.Load(envFileArg);
    dotEnvInfo = $"Loaded .env file: {envFileArg} ({loaded} variable(s) set)";
}
else
{
    if (DotEnvLoader.TryLoad(".env", out var loaded))
        dotEnvDebug = $"Auto-detected .env in current directory ({loaded} variable(s) set)";
}

// Parse log level early from args before DI is built
var verbose = args.Contains("--verbose") || args.Contains("-v");
var serilogLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;

// Disable ANSI colors if --no-color flag or NO_COLOR env var is set (https://no-color.org/)
var noColor = args.Contains("--no-color")
    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

if (noColor)
    AnsiConsole.Profile.Capabilities.Ansi = false;

// GCP structured JSON logging: --gcp-logs flag, or auto-detected in Cloud Run (K_SERVICE)
var gcpLogs = args.Contains("--gcp-logs")
    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("K_SERVICE"));

// Optional file logging: --log-file <path>
var logFile = args.Contains("--log-file")
    ? args.SkipWhile(a => a != "--log-file").Skip(1).FirstOrDefault()
    : null;

var logConfig = new LoggerConfiguration().MinimumLevel.Is(serilogLevel);

if (gcpLogs)
{
    logConfig.WriteTo.Console(new RenderedCompactJsonFormatter());
    // Suppress AnsiConsole entirely when outputting JSON logs
    AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(TextWriter.Null)
    });
}
else if (verbose)
{
    // In verbose mode, emit structured debug logs to stderr so they don't
    // interleave with AnsiConsole's stdout output.
    logConfig.WriteTo.Console(
        outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
        standardErrorFromLevel: LogEventLevel.Verbose);
}

if (logFile is not null)
    logConfig.WriteTo.File(
        logFile,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Infinite,
        shared: false);

Log.Logger = logConfig.CreateLogger();

if (dotEnvInfo is not null)
    Log.Information(dotEnvInfo);
if (dotEnvDebug is not null)
    Log.Debug(dotEnvDebug);

var services = new ServiceCollection();
services.AddLogging(b => b
    .ClearProviders()
    .AddSerilog(dispose: true));

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? "unknown";

app.Configure(config =>
{
    config.SetApplicationName("dns-sync");
    config.SetApplicationVersion(version);

    // Clean up help colors: replace hard-to-read yellow headers and
    // barely-visible dim gray command names with readable alternatives.
    var defaultStyles = HelpProviderStyle.Default;
    config.Settings.HelpProviderStyles = new HelpProviderStyle
    {
        Description = defaultStyles.Description,
        Usage = defaultStyles.Usage,
        Examples = defaultStyles.Examples,
        Arguments = defaultStyles.Arguments,
        Options = defaultStyles.Options,
        Commands = new CommandStyle
        {
            Header = new Style(Color.White, decoration: Decoration.Bold),
            ChildCommand = new Style(Color.White),
            RequiredArgument = defaultStyles.Commands?.RequiredArgument,
        },
    };

    config.AddCommand<ValidateCommand>("validate")
        .WithDescription("Validate config and zone files without making network calls")
        .WithExample(["validate", "--config", "config.yaml"]);

    config.AddCommand<PlanCommand>("plan")
        .WithDescription("Show what changes would be applied (no-op)")
        .WithExample(["plan", "--config", "config.yaml"]);

    config.AddCommand<ApplyCommand>("apply")
        .WithDescription("Apply changes from source to all target providers")
        .WithExample(["apply", "--config", "config.yaml", "--yes"]);
});

return await app.RunAsync(args);
