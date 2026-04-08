using DnsSync.Commands;
using DnsSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Spectre.Console;
using Spectre.Console.Cli;

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

if (logFile is not null)
    logConfig.WriteTo.File(
        logFile,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Infinite,
        shared: false);

Log.Logger = logConfig.CreateLogger();

var services = new ServiceCollection();
services.AddLogging(b => b
    .ClearProviders()
    .AddSerilog(dispose: true));

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("dns-sync");
    config.SetApplicationVersion("1.0.0");

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
