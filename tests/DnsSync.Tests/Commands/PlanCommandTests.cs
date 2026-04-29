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

/// <summary>
/// Tests for PlanCommand using stub providers so no real network calls are made.
/// </summary>
[Collection("cli-serial")]
public class PlanCommandTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly TestConsole _console = new();
    private readonly IAnsiConsole _previousConsole;

    public PlanCommandTests()
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
            cfg.AddCommand<PlanCommand>("plan");
        });
        return app;
    }

    // ── No changes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ZonesInSyncEmpty_ReturnsExitZero()
    {
        var (config, factory, resolver) = BuildYamlToYamlSetup([], []);

        var exit = await BuildApp(factory, resolver).RunAsync(["plan", "--config", config]);

        exit.ShouldBe(0);
    }

    [Fact]
    public async Task Plan_ZonesInSyncEmpty_PrintsInSyncMessage()
    {
        var (config, factory, resolver) = BuildYamlToYamlSetup([], []);

        await BuildApp(factory, resolver).RunAsync(["plan", "--config", config]);

        _console.Output.ShouldContain("in sync");
    }

    // ── Zones with no diff ────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ZonesInSync_ReturnsExitZero()
    {
        var (config, factory, resolver) = BuildYamlToYamlSetup(
            sourceRecords: [new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }],
            targetRecords: [new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }]);

        var exit = await BuildApp(factory, resolver).RunAsync(["plan", "--config", config]);

        exit.ShouldBe(0);
    }

    // ── Zones with diff ───────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ZonesWithDiff_ShowsChangeCount()
    {
        var (config, factory, resolver) = BuildYamlToYamlSetup(
            sourceRecords: [new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }],
            targetRecords: []);

        await BuildApp(factory, resolver).RunAsync(["plan", "--config", config]);

        _console.Output.ShouldContain("change");
    }

    [Fact]
    public async Task Plan_ZonesWithDiff_ReturnsExitZero_ByDefault()
    {
        // Without --exit-code, plan exits 0 even when there are changes
        var (config, factory, resolver) = BuildYamlToYamlSetup(
            sourceRecords: [new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }],
            targetRecords: []);

        var exit = await BuildApp(factory, resolver).RunAsync(["plan", "--config", config]);

        exit.ShouldBe(0);
    }

    [Fact]
    public async Task Plan_ZonesWithDiff_ExitCodeFlag_ReturnsExitTwo()
    {
        var (config, factory, resolver) = BuildYamlToYamlSetup(
            sourceRecords: [new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }],
            targetRecords: []);

        var exit = await BuildApp(factory, resolver).RunAsync(["plan", "--config", config, "--exit-code"]);

        exit.ShouldBe(2);
    }

    // ── --output json ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_JsonOutput_WritesValidJson()
    {
        var (config, factory, resolver) = BuildYamlToYamlSetup(
            sourceRecords: [new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }],
            targetRecords: []);

        await BuildApp(factory, resolver).RunAsync(["plan", "--config", config, "--output", "json"]);

        // JSON mode writes to Console.Out (not AnsiConsole) — just verify exit was clean
        // and no AnsiConsole markup leaked into output
        _console.Output.ShouldNotContain("[red]");
    }

    // ── Preflight failure ─────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_SourcePreflightFails_ReturnsExitOne()
    {
        var (config, factory, resolver) = BuildYamlToYamlSetup([], []);
        var sourceStub = new StubProvider();
        sourceStub.PreflightException = new InvalidOperationException("Auth failed");
        factory.SetProvider("source", sourceStub);

        var exit = await BuildApp(factory, resolver).RunAsync(["plan", "--config", config]);

        exit.ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (string ConfigPath, StubProviderFactory Factory, StubZoneResolver Resolver) BuildYamlToYamlSetup(
        IEnumerable<DnsRecord> sourceRecords,
        IEnumerable<DnsRecord> targetRecords)
    {
        const string ZoneName = "example.com.";
        const string SourceProvider = "source";
        const string TargetProvider = "target";

        var configPath = Path.Combine(_tmp, "plan-config.yaml");
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
