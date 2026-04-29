using DnsSync.Commands;
using DnsSync.Config;
using DnsSync.Core;
using DnsSync.Infrastructure;
using DnsSync.Providers;
using DnsSync.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace DnsSync.Tests.Cli;

/// <summary>
/// CLI integration tests that exercise the full command routing, settings
/// parsing, config loading, and error handling paths without network calls.
///
/// Approach: build a real CommandApp (same registration as Program.cs) and
/// run it via RunAsync(). AnsiConsole.Console is redirected to a TestConsole
/// so output can be captured. Tests are serialized via [Collection] to avoid
/// races on the static AnsiConsole.Console property.
///
/// Key cases:
/// - Unknown commands (dns-sync kajshka) → non-zero exit
/// - Invalid --config paths → exit 1
/// - --zone with values not in config → exit 1
/// - --zone with weird / malicious characters → exit 1
/// - Invalid --output values → exit 1
/// - Valid yaml→yaml config → exit 0
/// </summary>
[Collection("cli-serial")]   // serialize all CLI tests — they share static AnsiConsole.Console
public class CliIntegrationTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly TestConsole _console = new();
    private readonly IAnsiConsole _previousConsole;

    public CliIntegrationTests()
    {
        Directory.CreateDirectory(_tmp);
        _previousConsole = AnsiConsole.Console;
        AnsiConsole.Console = _console;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _previousConsole;
        if (Directory.Exists(_tmp))
            Directory.Delete(_tmp, recursive: true);
    }

    // ── App factory ───────────────────────────────────────────────────────────

    private static CommandApp BuildApp(IZoneResolver? resolver = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(resolver ?? new StubZoneResolver());
        services.AddSingleton<IProviderFactory, DefaultProviderFactory>();

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.SetApplicationName("dns-sync");
            // No PropagateExceptions() — Spectre converts parse errors and unhandled
            // command exceptions to non-zero exit codes, which is what the tests assert.

            config.AddCommand<ValidateCommand>("validate");
            config.AddCommand<PlanCommand>("plan");
            config.AddCommand<ApplyCommand>("apply");
            config.AddCommand<ImportCommand>("import");
            config.AddCommand<DiffCommand>("diff");
            config.AddCommand<DriftCommand>("drift");
        });

        return app;
    }

    private Task<int> Run(params string[] args) => BuildApp().RunAsync(args);

    private Task<int> RunWithResolver(IZoneResolver resolver, params string[] args)
        => BuildApp(resolver).RunAsync(args);

    // ── Unknown / misspelled commands ─────────────────────────────────────────

    [Fact]
    public async Task UnknownCommand_ReturnsNonZeroExitCode()
    {
        var exit = await Run("kajshka");
        exit.ShouldNotBe(0);
    }

    [Fact]
    public async Task UnknownCommand_WithFlags_ReturnsNonZeroExitCode()
    {
        var exit = await Run("foobarbaz", "--config", "config.yaml");
        exit.ShouldNotBe(0);
    }

    [Fact]
    public async Task UnknownCommand_LooksLikeSubcommand_ReturnsNonZeroExitCode()
    {
        var exit = await Run("appl");   // close to "apply" but not a match
        exit.ShouldNotBe(0);
    }

    // ── Invalid --config paths ────────────────────────────────────────────────

    [Fact]
    public async Task Validate_ConfigFileNotFound_ReturnsExitOne()
    {
        var exit = await Run("validate", "--config", "/no/such/path/config.yaml");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Plan_ConfigFileNotFound_ReturnsExitOne()
    {
        var exit = await Run("plan", "--config", "/no/such/path/config.yaml");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Validate_ConfigIsDirectory_ReturnsExitOne()
    {
        var exit = await Run("validate", "--config", _tmp);
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Validate_ConfigInvalidYaml_ReturnsExitOne()
    {
        var path = Path.Combine(_tmp, "bad.yaml");
        await File.WriteAllTextAsync(path, "{{{{ not: valid: yaml: [[");

        var exit = await Run("validate", "--config", path);
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Validate_ConfigMissingProvider_ReturnsExitOne()
    {
        var path = Path.Combine(_tmp, "no-provider.yaml");
        await File.WriteAllTextAsync(path, """
            providers: {}
            zones:
              example.com.:
                source: ghost
                targets: []
            """);

        var exit = await Run("validate", "--config", path);
        exit.ShouldBe(1);
    }

    // ── --zone flag validation ────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ZoneNotInConfig_ReturnsExitOne()
    {
        var config = WriteMinimalConfig();
        var exit = await Run("plan", "--config", config, "--zone", "not-in-config.example.com.");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Plan_ZoneWithExclamationMarks_ReturnsExitOne()
    {
        var config = WriteMinimalConfig();
        var exit = await Run("plan", "--config", config, "--zone", "!!not-a-valid-zone!!");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Plan_ZoneWithPathTraversal_ReturnsExitOne()
    {
        var config = WriteMinimalConfig();
        var exit = await Run("plan", "--config", config, "--zone", "../../etc/passwd");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Plan_ZoneWithUnicodeCharacters_ReturnsExitOne()
    {
        var config = WriteMinimalConfig();
        var exit = await Run("plan", "--config", config, "--zone", "例え.テスト.");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Plan_ZoneEmptyString_ReturnsExitOne()
    {
        var config = WriteMinimalConfig();
        var exit = await Run("plan", "--config", config, "--zone", "   ");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Drift_ZoneNotInConfig_ReturnsExitOne()
    {
        var config = WriteMinimalConfig();
        var exit = await Run("drift", "--config", config, "--zone", "ghost.example.com.");
        exit.ShouldBe(1);
    }

    // ── Invalid --output values ───────────────────────────────────────────────

    [Fact]
    public async Task Plan_InvalidOutputFormat_ReturnsExitOne()
    {
        var config = WriteMinimalConfig();
        var exit = await Run("plan", "--config", config, "--output", "garbage");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Drift_InvalidOutputFormat_ReturnsExitOne()
    {
        var config = WriteMinimalConfig();
        var exit = await Run("drift", "--config", config, "--output", "notaformat");
        exit.ShouldBe(1);
    }

    // ── Unknown flags ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_UnknownFlag_ReturnsNonZeroExitCode()
    {
        var exit = await Run("validate", "--totally-made-up-flag");
        exit.ShouldNotBe(0);
    }

    // ── Successful paths (yaml→yaml, no network) ──────────────────────────────

    [Fact]
    public async Task Validate_ValidYamlConfig_ReturnsExitZero()
    {
        var config = WriteYamlConfig();
        var exit = await Run("validate", "--config", config);
        exit.ShouldBe(0);
    }

    [Fact]
    public async Task Validate_ValidConfig_OutputContainsValidKeyword()
    {
        var config = WriteYamlConfig();
        await Run("validate", "--config", config);
        _console.Output.ShouldContain("valid");
    }

    [Fact]
    public async Task Validate_ValidConfig_StrictMode_ReturnsExitZero()
    {
        var config = WriteYamlConfig();
        var exit = await Run("validate", "--config", config, "--strict");
        exit.ShouldBe(0);
    }

    [Fact]
    public async Task Plan_ValidYamlConfig_NoZones_ReturnsExitZero()
    {
        // Minimal config with no zones — plan has nothing to do, exits 0
        var config = WriteMinimalConfig();
        var exit = await Run("plan", "--config", config);
        exit.ShouldBe(0);
    }

    // ── Output message assertions for error paths ─────────────────────────────

    [Fact]
    public async Task Validate_ConfigNotFound_OutputContainsErrorIndicator()
    {
        await Run("validate", "--config", "/no/such/config.yaml");
        // Error message should contain something actionable
        _console.Output.ShouldNotBeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Structurally valid config with one yaml→yaml zone.
    /// The zone has source and a target so it passes ValidateStructure.
    /// StubZoneResolver returns no zones so commands exit cleanly without network calls.
    /// </summary>
    private string WriteMinimalConfig()
    {
        var zonesDir = Path.Combine(_tmp, "zones");
        Directory.CreateDirectory(zonesDir);
        var configPath = Path.Combine(_tmp, "config.yaml");
        File.WriteAllText(configPath,
            "providers:\n" +
            $"  src:\n    type: yaml\n    directory: {zonesDir}\n" +
            $"  dst:\n    type: yaml\n    directory: {zonesDir}\n" +
            "zones:\n  example.com.:\n    source: src\n    targets: [dst]\n");
        return configPath;
    }

    /// <summary>
    /// Valid config with one yaml zone and a minimal zone file.
    /// Used for tests that need end-to-end validate to succeed.
    /// </summary>
    private string WriteYamlConfig()
    {
        var zonesDir = Path.Combine(_tmp, "zones");
        Directory.CreateDirectory(zonesDir);

        File.WriteAllText(Path.Combine(zonesDir, "example.com.yaml"), """
            $origin example.com.
            $ttl 300

            @ 300 IN A 1.2.3.4
            """);

        var configPath = Path.Combine(_tmp, "config.yaml");
        File.WriteAllText(configPath,
            "providers:\n" +
            $"  src:\n    type: yaml\n    directory: {zonesDir}\n" +
            $"  dst:\n    type: yaml\n    directory: {zonesDir}\n" +
            "zones:\n  example.com.:\n    source: src\n    targets: [dst]\n");
        return configPath;
    }
}

/// <summary>Ensures CLI tests that share static AnsiConsole.Console run serially.</summary>
[CollectionDefinition("cli-serial")]
public class CliSerialCollection { }
