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
        config.Providers["yaml_source"].Directory.ShouldBe("./zones");
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

    private static DnsSyncConfig Deserialize(string yaml)
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, yaml);
        try { return ConfigLoader.Load(tmp); }
        finally { File.Delete(tmp); }
    }
}
