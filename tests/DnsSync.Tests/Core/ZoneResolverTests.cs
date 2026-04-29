using DnsSync.Config;
using DnsSync.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DnsSync.Tests.Core;

/// <summary>
/// Tests for ZoneResolver — merging explicit zones with zone_groups discovery.
/// Uses YamlProvider with temp directories to avoid requiring external API credentials.
/// </summary>
public class ZoneResolverTests : IDisposable
{
    private readonly string _dir;
    private readonly ZoneResolver _resolver;

    public ZoneResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dns-sync-zr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _resolver = new ZoneResolver(NullLoggerFactory.Instance);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── helpers ──────────────────────────────────────────────────────────────

    private void CreateZoneFile(string fqdn) =>
        File.WriteAllText(
            Path.Combine(_dir, fqdn.TrimEnd('.') + ".yaml"),
            $"'': \n  type: A\n  ttl: 300\n  value: 1.2.3.4\n");

    private DnsSyncConfig ConfigWithYamlGroup(
        string? includePattern = null,
        string? excludePattern = null,
        string groupSource = "yaml_src") =>
        new()
        {
            Providers = new()
            {
                ["yaml_src"] = new() { Type = "yaml", Directory = _dir },
                ["cf"] = new() { Type = "cloudflare", ApiToken = "tok" },
            },
            ZoneGroups = new()
            {
                ["all"] = new()
                {
                    Source = groupSource,
                    Targets = ["cf"],
                    IncludePattern = includePattern,
                    ExcludePattern = excludePattern,
                }
            }
        };

    // ── no zone_groups ────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NoZoneGroups_ReturnsOnlyExplicitZones()
    {
        var config = new DnsSyncConfig
        {
            Providers = new() { ["cf"] = new() { Type = "cloudflare", ApiToken = "tok" } },
            Zones = new() { ["example.com."] = new() { Source = "cf", Targets = ["cf"] } }
        };

        var result = await _resolver.ResolveAsync(config, default);

        result.Count.ShouldBe(1);
        result.ContainsKey("example.com.").ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_EmptyConfig_ReturnsEmptyDictionary()
    {
        var result = await _resolver.ResolveAsync(new DnsSyncConfig(), default);
        result.ShouldBeEmpty();
    }

    // ── basic discovery ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ZoneGroupWithMatchingFiles_DiscoversAllZones()
    {
        CreateZoneFile("example.com.");
        CreateZoneFile("other.net.");
        var config = ConfigWithYamlGroup();

        var result = await _resolver.ResolveAsync(config, default);

        result.ContainsKey("example.com.").ShouldBeTrue();
        result.ContainsKey("other.net.").ShouldBeTrue();
        result["example.com."].Source.ShouldBe("yaml_src");
        result["example.com."].Targets.ShouldContain("cf");
    }

    [Fact]
    public async Task ResolveAsync_EmptyDirectory_ReturnsNoDiscoveredZones()
    {
        var config = ConfigWithYamlGroup();
        var result = await _resolver.ResolveAsync(config, default);
        result.ShouldBeEmpty();
    }

    // ── explicit zones take precedence ────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ExplicitZoneOverridesGroupZone_UsesExplicitTargets()
    {
        CreateZoneFile("example.com.");
        var config = ConfigWithYamlGroup();
        // Override with different targets list to verify which one wins
        config.Providers["cf2"] = new() { Type = "cloudflare", ApiToken = "tok2" };
        config.Zones["example.com."] = new() { Source = "yaml_src", Targets = ["cf2"] };

        var result = await _resolver.ResolveAsync(config, default);

        result.ContainsKey("example.com.").ShouldBeTrue();
        result["example.com."].Targets.ShouldContain("cf2");
        result["example.com."].Targets.ShouldNotContain("cf");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitZonePlusGroupZones_MergesWithoutDuplicates()
    {
        CreateZoneFile("discovered.com.");
        var config = ConfigWithYamlGroup();
        config.Zones["explicit.com."] = new() { Source = "yaml_src", Targets = ["cf"] };

        var result = await _resolver.ResolveAsync(config, default);

        result.ContainsKey("explicit.com.").ShouldBeTrue();
        result.ContainsKey("discovered.com.").ShouldBeTrue();
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_ZoneInBothExplicitAndGroup_ExactlyOneEntry()
    {
        CreateZoneFile("example.com.");
        var config = ConfigWithYamlGroup();
        config.Zones["example.com."] = new() { Source = "yaml_src", Targets = ["cf"] };

        var result = await _resolver.ResolveAsync(config, default);

        result.Count(kvp => string.Equals(kvp.Key, "example.com.", StringComparison.OrdinalIgnoreCase))
              .ShouldBe(1);
    }

    // ── include_pattern ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_IncludePattern_OnlyIncludesMatchingZones()
    {
        CreateZoneFile("keep.com.");
        CreateZoneFile("skip.net.");
        var config = ConfigWithYamlGroup(includePattern: @"\.com\.$");

        var result = await _resolver.ResolveAsync(config, default);

        result.ContainsKey("keep.com.").ShouldBeTrue();
        result.ContainsKey("skip.net.").ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_IncludePattern_NoMatches_ReturnsEmpty()
    {
        CreateZoneFile("example.com.");
        var config = ConfigWithYamlGroup(includePattern: @"\.io\.$");

        var result = await _resolver.ResolveAsync(config, default);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_IncludePattern_MatchesAll_ReturnsAll()
    {
        CreateZoneFile("alpha.com.");
        CreateZoneFile("beta.net.");
        var config = ConfigWithYamlGroup(includePattern: ".*");

        var result = await _resolver.ResolveAsync(config, default);

        result.Count.ShouldBe(2);
    }

    // ── exclude_pattern ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ExcludePattern_RemovesMatchingZones()
    {
        CreateZoneFile("prod.com.");
        CreateZoneFile("staging.com.");
        var config = ConfigWithYamlGroup(excludePattern: "^staging");

        var result = await _resolver.ResolveAsync(config, default);

        result.ContainsKey("prod.com.").ShouldBeTrue();
        result.ContainsKey("staging.com.").ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_ExcludePattern_ExcludesAll_ReturnsEmpty()
    {
        CreateZoneFile("example.com.");
        var config = ConfigWithYamlGroup(excludePattern: ".*");

        var result = await _resolver.ResolveAsync(config, default);

        result.ShouldBeEmpty();
    }

    // ── include + exclude combined ────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_IncludeAndExclude_ExcludeTakesPrecedenceWithinIncludedSet()
    {
        CreateZoneFile("prod.com.");
        CreateZoneFile("staging.com.");
        CreateZoneFile("dev.net.");
        var config = ConfigWithYamlGroup(
            includePattern: @"\.com\.$",
            excludePattern: "^staging");

        var result = await _resolver.ResolveAsync(config, default);

        result.ContainsKey("prod.com.").ShouldBeTrue();
        result.ContainsKey("staging.com.").ShouldBeFalse(); // excluded
        result.ContainsKey("dev.net.").ShouldBeFalse();      // not included
    }

    // ── case insensitivity ────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ZoneKeys_CaseInsensitiveDeduplication()
    {
        CreateZoneFile("example.com.");
        var config = ConfigWithYamlGroup();
        // Explicit key in different case
        config.Zones["EXAMPLE.COM."] = new() { Source = "yaml_src", Targets = ["cf"] };

        var result = await _resolver.ResolveAsync(config, default);

        result.Count(kvp => kvp.Key.Equals("example.com.", StringComparison.OrdinalIgnoreCase))
              .ShouldBe(1);
    }

    // ── multiple zone groups ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_MultipleZoneGroups_AllDiscovered()
    {
        var dir2 = Path.Combine(Path.GetTempPath(), "dns-sync-zr2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir2);
        try
        {
            CreateZoneFile("from-dir1.com.");
            File.WriteAllText(Path.Combine(dir2, "from-dir2.com.yaml"),
                "'': \n  type: A\n  ttl: 300\n  value: 2.2.2.2\n");

            var config = new DnsSyncConfig
            {
                Providers = new()
                {
                    ["yaml1"] = new() { Type = "yaml", Directory = _dir },
                    ["yaml2"] = new() { Type = "yaml", Directory = dir2 },
                    ["cf"] = new() { Type = "cloudflare", ApiToken = "tok" },
                },
                ZoneGroups = new()
                {
                    ["g1"] = new() { Source = "yaml1", Targets = ["cf"] },
                    ["g2"] = new() { Source = "yaml2", Targets = ["cf"] },
                }
            };

            var result = await _resolver.ResolveAsync(config, default);

            result.ContainsKey("from-dir1.com.").ShouldBeTrue();
            result.ContainsKey("from-dir2.com.").ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir2, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_MultipleGroups_SameZoneInBoth_FirstGroupWins()
    {
        var dir2 = Path.Combine(Path.GetTempPath(), "dns-sync-zr3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir2);
        try
        {
            CreateZoneFile("shared.com.");
            File.WriteAllText(Path.Combine(dir2, "shared.com.yaml"),
                "'': \n  type: A\n  ttl: 300\n  value: 2.2.2.2\n");
            var config = new DnsSyncConfig
            {
                Providers = new()
                {
                    ["yaml1"] = new() { Type = "yaml", Directory = _dir },
                    ["yaml2"] = new() { Type = "yaml", Directory = dir2 },
                    ["cf"] = new() { Type = "cloudflare", ApiToken = "tok" },
                    ["pb"] = new() { Type = "porkbun", ApiKey = "k", SecretKey = "s" },
                },
                ZoneGroups = new()
                {
                    ["g1"] = new() { Source = "yaml1", Targets = ["cf"] },
                    ["g2"] = new() { Source = "yaml2", Targets = ["pb"] },
                }
            };

            var result = await _resolver.ResolveAsync(config, default);

            // shared.com. should appear exactly once
            result.Count(kvp => kvp.Key.Equals("shared.com.", StringComparison.OrdinalIgnoreCase))
                  .ShouldBe(1);
            // First group wins → targets = ["cf"]
            result["shared.com."].Targets.ShouldContain("cf");
        }
        finally
        {
            Directory.Delete(dir2, recursive: true);
        }
    }

    // ── failure resilience ────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ProviderDirectoryMissing_DoesNotThrow_ReturnsExplicitZones()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["missing"] = new() { Type = "yaml", Directory = "/nonexistent-xyz-path" },
                ["cf"] = new() { Type = "cloudflare", ApiToken = "tok" },
            },
            Zones = new() { ["explicit.com."] = new() { Source = "cf", Targets = ["cf"] } },
            ZoneGroups = new() { ["bad"] = new() { Source = "missing", Targets = ["cf"] } }
        };

        // Must not throw — warns and continues
        var result = await _resolver.ResolveAsync(config, default);

        result.ContainsKey("explicit.com.").ShouldBeTrue();
    }

    // ── cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_CancelledToken_CompletesOrThrowsOperationCancelled()
    {
        CreateZoneFile("example.com.");
        var config = ConfigWithYamlGroup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // YAML provider does not respect CT on file reads — acceptable;
        // just ensure no unrelated exception escapes.
        var ex = await Record.ExceptionAsync(() => _resolver.ResolveAsync(config, cts.Token));

        (ex is null || ex is OperationCanceledException).ShouldBeTrue();
    }
}
