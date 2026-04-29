using DnsSync.Config;
using Shouldly;

namespace DnsSync.Tests.Config;

public class ConfigLoaderTests
{
    private const string ValidYaml = """
        providers:
          yaml_source:
            type: yaml
            directory: ./zones
          cloudflare:
            type: cloudflare
            api_token: my-token
        zones:
          example.com.:
            source: yaml_source
            targets:
              - cloudflare
        """;

    [Fact]
    public void Load_ValidYaml_ParsesProviders()
    {
        var config = Deserialize(ValidYaml);

        config.Providers.Count.ShouldBe(2);
        config.Providers.ShouldContainKey("yaml_source");
        config.Providers["yaml_source"].Type.ShouldBe("yaml");
        // Directory is resolved to an absolute path relative to the config file location
        config.Providers["yaml_source"].Directory.ShouldNotBeNullOrEmpty();
        Path.IsPathRooted(config.Providers["yaml_source"].Directory).ShouldBeTrue();
        config.Providers["yaml_source"].Directory.ShouldEndWith("zones");
    }

    [Fact]
    public void Load_ValidYaml_ParsesZones()
    {
        var config = Deserialize(ValidYaml);

        config.Zones.Count.ShouldBe(1);
        config.Zones["example.com."].Source.ShouldBe("yaml_source");
        config.Zones["example.com."].Targets.ShouldHaveSingleItem().ShouldBe("cloudflare");
    }

    [Fact]
    public void InterpolateEnvVars_ReplacesEnvVar()
    {
        Environment.SetEnvironmentVariable("TEST_TOKEN_DNS_SYNC", "secret-abc");

        var input = "api_token: ${TEST_TOKEN_DNS_SYNC}";
        var result = ConfigLoader.InterpolateEnvVars(input);

        result.ShouldBe("api_token: secret-abc");

        Environment.SetEnvironmentVariable("TEST_TOKEN_DNS_SYNC", null);
    }

    [Fact]
    public void InterpolateEnvVars_ThrowsWhenEnvVarNotSet()
    {
        Environment.SetEnvironmentVariable("MISSING_VAR_DNS_SYNC", null);

        var input = "api_token: ${MISSING_VAR_DNS_SYNC}";

        var ex = Should.Throw<InvalidOperationException>(() => ConfigLoader.InterpolateEnvVars(input));
        ex.Message.ShouldContain("MISSING_VAR_DNS_SYNC");
    }

    [Fact]
    public void InterpolateEnvVars_DoesNotInterpolateCommentLines()
    {
        var input = "# This is ${NOT_A_VAR} and should not be touched";
        var result = ConfigLoader.InterpolateEnvVars(input);
        result.ShouldBe(input);
    }

    [Fact]
    public void ValidateStructure_ValidConfig_ReturnsNoErrors()
    {
        var config = Deserialize(ValidYaml);
        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateStructure_MissingProviderType_ReturnsError()
    {
        var config = Deserialize("""
            providers:
              bad_provider:
                directory: ./zones
            zones:
              example.com.:
                source: bad_provider
                targets: [bad_provider]
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("bad_provider") && e.Contains("missing") && e.Contains("type"));
    }

    [Fact]
    public void ValidateStructure_SameSourceAndTarget_ReturnsError()
    {
        var config = Deserialize("""
            providers:
              cf:
                type: cloudflare
                api_token: tok
            zones:
              example.com.:
                source: cf
                targets:
                  - cf
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("source and target"));
    }

    [Fact]
    public void ValidateStructure_UnknownSourceProvider_ReturnsError()
    {
        var config = Deserialize("""
            providers:
              cf:
                type: cloudflare
                api_token: tok
            zones:
              example.com.:
                source: does_not_exist
                targets: [cf]
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("does_not_exist"));
    }

    [Fact]
    public void Load_MultipleInstancesSameType_ParsesBothProviders()
    {
        var config = Deserialize("""
            providers:
              cf_org1:
                type: cloudflare
                api_token: token-org1
              cf_org2:
                type: cloudflare
                api_token: token-org2
            zones:
              example.com.:
                source: cf_org1
                targets:
                  - cf_org2
            """);

        config.Providers.Count.ShouldBe(2);
        config.Providers["cf_org1"].Type.ShouldBe("cloudflare");
        config.Providers["cf_org1"].ApiToken.ShouldBe("token-org1");
        config.Providers["cf_org2"].Type.ShouldBe("cloudflare");
        config.Providers["cf_org2"].ApiToken.ShouldBe("token-org2");
    }

    [Fact]
    public void Load_MultipleInstancesSameType_ParsesZoneMapping()
    {
        var config = Deserialize("""
            providers:
              cf_org1:
                type: cloudflare
                api_token: token-org1
              cf_org2:
                type: cloudflare
                api_token: token-org2
            zones:
              example.com.:
                source: cf_org1
                targets:
                  - cf_org2
              other.com.:
                source: cf_org2
                targets:
                  - cf_org1
            """);

        config.Zones.Count.ShouldBe(2);
        config.Zones["example.com."].Source.ShouldBe("cf_org1");
        config.Zones["example.com."].Targets.ShouldHaveSingleItem().ShouldBe("cf_org2");
        config.Zones["other.com."].Source.ShouldBe("cf_org2");
        config.Zones["other.com."].Targets.ShouldHaveSingleItem().ShouldBe("cf_org1");
    }

    [Fact]
    public void ValidateStructure_MultipleInstancesSameType_ReturnsNoErrors()
    {
        var config = Deserialize("""
            providers:
              cf_org1:
                type: cloudflare
                api_token: token-org1
              cf_org2:
                type: cloudflare
                api_token: token-org2
            zones:
              example.com.:
                source: cf_org1
                targets:
                  - cf_org2
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateStructure_CrossAccountSync_SourceFromOneTargetAnother_ReturnsNoErrors()
    {
        // Validates the cross-account pattern: read from cf_org1, write to cf_org2
        var config = Deserialize("""
            providers:
              cf_org1:
                type: cloudflare
                api_token: token-org1
              cf_org2:
                type: cloudflare
                api_token: token-org2
            zones:
              example.com.:
                source: cf_org1
                targets:
                  - cf_org2
              other.com.:
                source: cf_org2
                targets:
                  - cf_org1
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateStructure_SameInstanceAsSourceAndTarget_ReturnsError()
    {
        // Two Cloudflare instances — but one zone mistakenly uses same instance as source and target
        var config = Deserialize("""
            providers:
              cf_org1:
                type: cloudflare
                api_token: token-org1
              cf_org2:
                type: cloudflare
                api_token: token-org2
            zones:
              example.com.:
                source: cf_org1
                targets:
                  - cf_org1
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("source and target"));
    }

    [Fact]
    public void Load_MixedProviderTypes_ParseesAllInstances()
    {
        var config = Deserialize("""
            providers:
              yaml_source:
                type: yaml
                directory: ./zones
              cf_org1:
                type: cloudflare
                api_token: token-org1
              cf_org2:
                type: cloudflare
                api_token: token-org2
            zones:
              example.com.:
                source: yaml_source
                targets:
                  - cf_org1
                  - cf_org2
            """);

        config.Providers.Count.ShouldBe(3);
        config.Zones["example.com."].Targets.Count.ShouldBe(2);
        config.Zones["example.com."].Targets.ShouldContain("cf_org1");
        config.Zones["example.com."].Targets.ShouldContain("cf_org2");

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldBeEmpty();
    }

    // ── zone_groups: parsing ──────────────────────────────────────────────────

    [Fact]
    public void Load_ZoneGroups_ParsesCorrectly()
    {
        var config = Deserialize("""
            providers:
              yaml_src:
                type: yaml
                directory: ./zones
              cf:
                type: cloudflare
                api_token: tok
            zone_groups:
              all-com:
                source: yaml_src
                targets:
                  - cf
                include_pattern: ".*\\.com\\."
                exclude_pattern: "staging\\..*"
            """);

        config.ZoneGroups.Count.ShouldBe(1);
        config.ZoneGroups.ShouldContainKey("all-com");
        var g = config.ZoneGroups["all-com"];
        g.Source.ShouldBe("yaml_src");
        g.Targets.ShouldContain("cf");
        g.IncludePattern.ShouldBe(".*\\.com\\.");
        g.ExcludePattern.ShouldBe("staging\\..*");
    }

    [Fact]
    public void Load_ZoneGroups_WithoutPatterns_HasNullPatterns()
    {
        var config = Deserialize("""
            providers:
              yaml_src:
                type: yaml
                directory: ./zones
              cf:
                type: cloudflare
                api_token: tok
            zone_groups:
              all:
                source: yaml_src
                targets:
                  - cf
            """);

        var g = config.ZoneGroups["all"];
        g.IncludePattern.ShouldBeNull();
        g.ExcludePattern.ShouldBeNull();
    }

    // ── zone_groups: ValidateStructure ────────────────────────────────────────

    [Fact]
    public void ValidateStructure_ZoneGroups_ValidConfig_ReturnsNoErrors()
    {
        var config = Deserialize("""
            providers:
              yaml_src:
                type: yaml
                directory: ./zones
              cf:
                type: cloudflare
                api_token: tok
            zone_groups:
              all:
                source: yaml_src
                targets:
                  - cf
            """);

        ConfigLoader.ValidateStructure(config).ShouldBeEmpty();
    }

    [Fact]
    public void ValidateStructure_NoZonesButHasZoneGroups_IsValid()
    {
        var config = Deserialize("""
            providers:
              yaml_src:
                type: yaml
                directory: ./zones
              cf:
                type: cloudflare
                api_token: tok
            zone_groups:
              all:
                source: yaml_src
                targets:
                  - cf
            """);

        // zone_groups alone satisfies the "at least zones or zone_groups" requirement
        ConfigLoader.ValidateStructure(config).ShouldBeEmpty();
    }

    [Fact]
    public void ValidateStructure_ZoneGroups_UnknownSource_ReturnsError()
    {
        var config = Deserialize("""
            providers:
              cf:
                type: cloudflare
                api_token: tok
            zone_groups:
              all:
                source: nonexistent
                targets:
                  - cf
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("nonexistent") && e.Contains("source"));
    }

    [Fact]
    public void ValidateStructure_ZoneGroups_UnknownTarget_ReturnsError()
    {
        var config = Deserialize("""
            providers:
              yaml_src:
                type: yaml
                directory: ./zones
            zone_groups:
              all:
                source: yaml_src
                targets:
                  - ghost_provider
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("ghost_provider"));
    }

    [Fact]
    public void ValidateStructure_ZoneGroups_SameSourceAndTarget_ReturnsError()
    {
        var config = Deserialize("""
            providers:
              cf:
                type: cloudflare
                api_token: tok
            zone_groups:
              all:
                source: cf
                targets:
                  - cf
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("source and target") || e.Contains("both source"));
    }

    [Fact]
    public void ValidateStructure_ZoneGroups_ReadOnlyTarget_ReturnsError()
    {
        var config = Deserialize("""
            providers:
              yaml_src:
                type: yaml
                directory: ./zones
              readonly_cf:
                type: cloudflare
                api_token: tok
                readonly: true
            zone_groups:
              all:
                source: yaml_src
                targets:
                  - readonly_cf
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("read-only") || e.Contains("readonly"));
    }

    [Fact]
    public void ValidateStructure_ZoneGroups_EmptyTargets_ReturnsError()
    {
        var config = Deserialize("""
            providers:
              yaml_src:
                type: yaml
                directory: ./zones
            zone_groups:
              all:
                source: yaml_src
                targets: []
            """);

        var errors = ConfigLoader.ValidateStructure(config);
        errors.ShouldContain(e => e.Contains("target") || e.Contains("all"));
    }

    private static DnsSyncConfig Deserialize(string yaml)
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, yaml);
        try { return ConfigLoader.Load(tmp); }
        finally { File.Delete(tmp); }
    }
}
