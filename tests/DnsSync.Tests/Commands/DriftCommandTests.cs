using DnsSync.Commands;
using DnsSync.Config;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Infrastructure;
using DnsSync.Providers;
using DnsSync.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace DnsSync.Tests.Commands;

[Collection("cli-serial")]
public class DriftCommandTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly TestConsole _console = new();
    private readonly IAnsiConsole _previousConsole;

    public DriftCommandTests()
    {
        Directory.CreateDirectory(_tmp);
        _previousConsole = AnsiConsole.Console;
        AnsiConsole.Console = _console;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _previousConsole;
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private CommandApp BuildApp(StubProviderFactory factory, StubZoneResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IZoneResolver>(resolver);
        services.AddSingleton<IProviderFactory>(factory);

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);
        app.Configure(cfg =>
        {
            cfg.SetApplicationName("dns-sync");
            cfg.PropagateExceptions();
            cfg.AddCommand<DriftCommand>("drift");
        });
        return app;
    }

    // ── Zones in sync (empty) ─────────────────────────────────────────────────

    [Fact]
    public async Task Drift_EmptyZonesInSync_ReturnsExitZero()
    {
        var (config, factory, resolver) = BuildSetup([], []);

        var exit = await BuildApp(factory, resolver).RunAsync(["drift", "--config", config]);

        exit.ShouldBe(0);
    }

    // ── Zones in sync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Drift_ZonesInSync_ReturnsExitZero()
    {
        var record = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([record], [record]);

        var exit = await BuildApp(factory, resolver).RunAsync(["drift", "--config", config]);

        exit.ShouldBe(0);
    }

    [Fact]
    public async Task Drift_ZonesInSync_PrintsInSyncMessage()
    {
        var record = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([record], [record]);

        await BuildApp(factory, resolver).RunAsync(["drift", "--config", config]);

        _console.Output.ShouldContain("in sync");
    }

    // ── Drift detected ────────────────────────────────────────────────────────

    [Fact]
    public async Task Drift_DriftDetected_ReturnsExitOne()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);

        var exit = await BuildApp(factory, resolver).RunAsync(["drift", "--config", config]);

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Drift_DriftDetected_PrintsDriftMessage()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);

        await BuildApp(factory, resolver).RunAsync(["drift", "--config", config]);

        _console.Output.ShouldContain("Drift detected");
    }

    // ── --output json ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Drift_JsonOutput_ReturnsExitOneWhenDrift()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);

        var exit = await BuildApp(factory, resolver)
            .RunAsync(["drift", "--config", config, "--output", "json"]);

        exit.ShouldBe(1);
    }

    // ── --output silent ───────────────────────────────────────────────────────

    [Fact]
    public async Task Drift_SilentOutput_DriftDetected_ReturnsExitOne_NoDriftOutput()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);

        var exit = await BuildApp(factory, resolver)
            .RunAsync(["drift", "--config", config, "--output", "silent"]);

        exit.ShouldBe(1);
        // Silent mode suppresses zone-level drift output, but config loading still prints
        _console.Output.ShouldNotContain("Drift detected");
        _console.Output.ShouldNotContain("in sync");
    }

    // ── --ignore-ttl ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Drift_IgnoreTtl_TtlOnlyChange_ReturnsExitZero()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var target = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 600, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], [target]);

        var exit = await BuildApp(factory, resolver)
            .RunAsync(["drift", "--config", config, "--ignore-ttl"]);

        exit.ShouldBe(0);
    }

    // ── Preflight failure ─────────────────────────────────────────────────────

    [Fact]
    public async Task Drift_PreflightFails_ReturnsExitOne()
    {
        var (config, factory, resolver) = BuildSetup([], []);
        var sourceStub = new StubProvider();
        sourceStub.PreflightException = new InvalidOperationException("Auth failed");
        factory.SetProvider("source", sourceStub);

        var exit = await BuildApp(factory, resolver).RunAsync(["drift", "--config", config]);

        exit.ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (string ConfigPath, StubProviderFactory Factory, StubZoneResolver Resolver) BuildSetup(
        IEnumerable<DnsRecord> sourceRecords,
        IEnumerable<DnsRecord> targetRecords)
    {
        const string ZoneName = "example.com.";
        const string SourceProvider = "source";
        const string TargetProvider = "target";

        var configPath = Path.Combine(_tmp, $"config-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(configPath, $"""
            providers:
              {SourceProvider}:
                type: yaml
                directory: /tmp
              {TargetProvider}:
                type: yaml
                directory: /tmp
            zones:
              {ZoneName}:
                source: {SourceProvider}
                targets: [{TargetProvider}]
            """);

        var sourceStub = new StubProvider();
        sourceStub.SetZone(new DnsZone { Name = ZoneName, Records = sourceRecords.ToList() });

        var targetStub = new StubProvider();
        targetStub.SetZone(new DnsZone { Name = ZoneName, Records = targetRecords.ToList() });

        var factory = new StubProviderFactory();
        factory.SetProvider(SourceProvider, sourceStub);
        factory.SetProvider(TargetProvider, targetStub);

        var resolver = new StubZoneResolver(new Dictionary<string, ZoneConfig>(StringComparer.OrdinalIgnoreCase)
        {
            [ZoneName] = new ZoneConfig { Source = SourceProvider, Targets = [TargetProvider] }
        });

        return (configPath, factory, resolver);
    }
}
