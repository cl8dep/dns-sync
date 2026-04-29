using DnsSync.Config;
using DnsSync.Validation;
using Shouldly;

namespace DnsSync.Tests.Validation;

/// <summary>
/// Tests for ConfigValidator, which wraps ConfigLoader.ValidateStructure.
/// </summary>
public class ConfigValidatorTests
{
    // ── Valid configs ─────────────────────────────────────────────────────────

    [Fact]
    public void Validate_MinimalValidConfig_ReturnsNoErrors()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["src"] = new ProviderConfig { Type = "yaml", Directory = "/zones" },
                ["dst"] = new ProviderConfig { Type = "yaml", Directory = "/zones" },
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "src", Targets = ["dst"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_AllProviderTypes_NoErrors()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["cf"]      = new ProviderConfig { Type = "cloudflare",    ApiToken = "tok" },
                ["gcp"]     = new ProviderConfig { Type = "gcp_cloud_dns" },
                ["pb"]      = new ProviderConfig { Type = "porkbun",       ApiKey = "k", SecretKey = "s" },
                ["r53"]     = new ProviderConfig { Type = "route53" },
                ["gd"]      = new ProviderConfig { Type = "godaddy",       ApiKey = "k", SecretKey = "s" },
                ["local"]   = new ProviderConfig { Type = "yaml",          Directory = "/zones" },
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "cf", Targets = ["gcp"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeTrue();
    }

    // ── No providers ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoProviders_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new(),
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "src", Targets = ["dst"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("No providers"));
    }

    // ── No zones ──────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoZonesOrGroups_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["src"] = new ProviderConfig { Type = "yaml", Directory = "/zones" }
            },
            Zones = new()
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("No zones"));
    }

    // ── Unknown provider type ─────────────────────────────────────────────────

    [Fact]
    public void Validate_UnknownProviderType_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["p"] = new ProviderConfig { Type = "fakecloud" }
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "p", Targets = ["p"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("unknown type") && e.Contains("fakecloud"));
    }

    // ── Missing provider credentials ──────────────────────────────────────────

    [Fact]
    public void Validate_YamlProviderMissingDirectory_ReturnsError()
    {
        var config = MakeConfigWithProvider(new ProviderConfig { Type = "yaml" });

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("directory"));
    }

    [Fact]
    public void Validate_CloudflareProviderMissingToken_ReturnsError()
    {
        var config = MakeConfigWithProvider(new ProviderConfig { Type = "cloudflare" });

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("api_token"));
    }

    [Fact]
    public void Validate_PorkbunProviderMissingKeys_ReturnsTwoErrors()
    {
        var config = MakeConfigWithProvider(new ProviderConfig { Type = "porkbun" });

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("api_key"));
        result.Errors.ShouldContain(e => e.Contains("secret_key"));
    }

    [Fact]
    public void Validate_GoDaddyProviderMissingKeys_ReturnsTwoErrors()
    {
        var config = MakeConfigWithProvider(new ProviderConfig { Type = "godaddy" });

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("api_key"));
        result.Errors.ShouldContain(e => e.Contains("secret_key"));
    }

    [Fact]
    public void Validate_GcpProviderNoRequiredFields_IsValid()
    {
        // gcp_cloud_dns has no required config fields (uses ADC / env vars)
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["src"] = new ProviderConfig { Type = "yaml", Directory = "/zones" },
                ["gcp"] = new ProviderConfig { Type = "gcp_cloud_dns" },
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "src", Targets = ["gcp"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeTrue();
    }

    // ── Zone validation ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_ZoneNoSource_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["dst"] = new ProviderConfig { Type = "yaml", Directory = "/zones" }
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "", Targets = ["dst"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("no source provider"));
    }

    [Fact]
    public void Validate_ZoneUnknownSource_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["dst"] = new ProviderConfig { Type = "yaml", Directory = "/zones" }
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "ghost", Targets = ["dst"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("unknown source provider") && e.Contains("ghost"));
    }

    [Fact]
    public void Validate_ZoneNoTargets_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["src"] = new ProviderConfig { Type = "yaml", Directory = "/zones" }
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "src", Targets = [] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("no target providers"));
    }

    [Fact]
    public void Validate_ZoneUnknownTarget_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["src"] = new ProviderConfig { Type = "yaml", Directory = "/zones" }
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "src", Targets = ["ghost"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("unknown target provider") && e.Contains("ghost"));
    }

    [Fact]
    public void Validate_ZoneSourceEqualsTarget_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["p"] = new ProviderConfig { Type = "yaml", Directory = "/zones" }
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "p", Targets = ["p"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("both source and target"));
    }

    [Fact]
    public void Validate_ZoneTargetIsReadOnly_ReturnsError()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["src"] = new ProviderConfig { Type = "yaml", Directory = "/zones" },
                ["dst"] = new ProviderConfig { Type = "yaml", Directory = "/zones", ReadOnly = true },
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "src", Targets = ["dst"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("read-only"));
    }

    // ── Multiple errors accumulate ────────────────────────────────────────────

    [Fact]
    public void Validate_MultipleProblems_AccumulatesAllErrors()
    {
        var config = new DnsSyncConfig
        {
            Providers = new()
            {
                ["cf"] = new ProviderConfig { Type = "cloudflare" },   // missing token
                ["pb"] = new ProviderConfig { Type = "porkbun" },      // missing api_key + secret_key
            },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "cf", Targets = ["pb"] }
            }
        };

        var result = ConfigValidator.Validate(config);

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBeGreaterThanOrEqualTo(3); // token + api_key + secret_key
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal config with a single provider named "p" and a zone that
    /// references it as both source and a target (allows testing provider-level
    /// errors in isolation — zone errors may also appear but are separate).
    /// </summary>
    private static DnsSyncConfig MakeConfigWithProvider(ProviderConfig provider)
    {
        return new DnsSyncConfig
        {
            Providers = new() { ["p"] = provider },
            Zones = new()
            {
                ["example.com."] = new ZoneConfig { Source = "p", Targets = ["p"] }
            }
        };
    }
}
