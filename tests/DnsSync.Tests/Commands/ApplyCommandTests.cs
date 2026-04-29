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
public class ApplyCommandTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly TestConsole _console = new();
    private readonly IAnsiConsole _previousConsole;

    public ApplyCommandTests()
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
            cfg.AddCommand<ApplyCommand>("apply");
        });
        return app;
    }

    // ── Nothing to apply ─────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_ZonesInSync_ReturnsExitZero()
    {
        var record = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([record], [record]);

        var exit = await BuildApp(factory, resolver)
            .RunAsync(["apply", "--config", config, "--yes"]);

        exit.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_ZonesInSync_PrintsNothingToApplyMessage()
    {
        var record = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([record], [record]);

        await BuildApp(factory, resolver).RunAsync(["apply", "--config", config, "--yes"]);

        _console.Output.ShouldContain("in sync");
    }

    // ── Changes applied ───────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_WithChanges_Yes_CallsApplyOnProvider()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);

        // Grab the target stub that was set up by BuildSetup
        var targetStub = new StubProvider();
        targetStub.SetZone(new DnsZone { Name = "example.com.", Records = [] });
        targetStub.SetApplyResult(new ApplyResult(1, 0, []));
        factory.SetProvider("target", targetStub);

        await BuildApp(factory, resolver).RunAsync(["apply", "--config", config, "--yes"]);

        targetStub.ApplyCalls.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Apply_WithChanges_Yes_ReturnsExitZero()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);

        var targetStub = new StubProvider();
        targetStub.SetZone(new DnsZone { Name = "example.com.", Records = [] });
        targetStub.SetApplyResult(new ApplyResult(1, 0, []));
        factory.SetProvider("target", targetStub);

        var exit = await BuildApp(factory, resolver)
            .RunAsync(["apply", "--config", config, "--yes"]);

        exit.ShouldBe(0);
    }

    // ── --max-changes guard ───────────────────────────────────────────────────

    [Fact]
    public async Task Apply_ExceedsMaxChanges_WithoutForce_ReturnsExitOne()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);

        var exit = await BuildApp(factory, resolver)
            .RunAsync(["apply", "--config", config, "--yes", "--max-changes", "0"]);

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Apply_ExceedsMaxChanges_WithForce_Applies()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);

        var exit = await BuildApp(factory, resolver)
            .RunAsync(["apply", "--config", config, "--yes", "--force"]);

        exit.ShouldBe(0);
    }

    // ── Preflight failure ─────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_PreflightFails_ReturnsExitOne()
    {
        var source = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] };
        var (config, factory, resolver) = BuildSetup([source], []);
        // Make source preflight throw so preflight check fails
        ((StubProvider)factory.Default).PreflightException = new InvalidOperationException("Credentials invalid");
        var sourceStub = new StubProvider();
        sourceStub.PreflightException = new InvalidOperationException("Credentials invalid");
        factory.SetProvider("source", sourceStub);

        var exit = await BuildApp(factory, resolver)
            .RunAsync(["apply", "--config", config, "--yes"]);

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
        targetStub.SetApplyResult(new ApplyResult(1, 0, []));

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
