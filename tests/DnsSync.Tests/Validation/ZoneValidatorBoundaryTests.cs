using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Validation;
using Shouldly;

namespace DnsSync.Tests.Validation;

/// <summary>
/// Boundary and edge-case tests for ZoneValidator:
/// TTL limits, SRV/MX/CAA field boundaries, empty zone.
/// </summary>
public class ZoneValidatorBoundaryTests
{
    private static DnsZone ZoneWith(params DnsRecord[] records) =>
        new() { Name = "example.com.", Records = records };

    private static ARecord ARecord(int ttl) => new()
    {
        Name      = "www.example.com.",
        Type      = "A",
        Ttl       = ttl,
        Addresses = ["1.2.3.4"]
    };

    // ── empty zone ────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ZoneWithNoRecords_IsValid()
    {
        var result = ZoneValidator.Validate(new DnsZone { Name = "example.com.", Records = [] });
        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    // ── TTL boundaries ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_TtlZero_IsValid()
    {
        var result = ZoneValidator.Validate(ZoneWith(ARecord(0)));
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_TtlOne_ReturnsWarning()
    {
        var result = ZoneValidator.Validate(ZoneWith(ARecord(1)));
        result.Warnings.ShouldContain(w => w.Contains("TTL") || w.Contains("low") || w.Contains("1"));
    }

    [Fact]
    public void Validate_TtlAt59_ReturnsWarning()
    {
        var result = ZoneValidator.Validate(ZoneWith(ARecord(59)));
        result.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_TtlAt300_NoWarning()
    {
        var result = ZoneValidator.Validate(ZoneWith(ARecord(300)));
        result.Warnings.ShouldBeEmpty();
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_TtlAtMaxInt32_IsValid()
    {
        var result = ZoneValidator.Validate(ZoneWith(ARecord(int.MaxValue)));
        result.IsValid.ShouldBeTrue();
    }

    // ── SRV port boundaries ───────────────────────────────────────────────────

    [Fact]
    public void Validate_SrvPortZero_IsValid()
    {
        var zone = ZoneWith(new SrvRecord
        {
            Name   = "_sip._tcp.example.com.",
            Type   = "SRV",
            Ttl    = 600,
            Values = [new SrvValue(10, 20, 0, "sip.example.com.")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_SrvPortMax_IsValid()
    {
        var zone = ZoneWith(new SrvRecord
        {
            Name   = "_sip._tcp.example.com.",
            Type   = "SRV",
            Ttl    = 600,
            Values = [new SrvValue(10, 20, 65535, "sip.example.com.")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_SrvPortExceedsMax_ReturnsError()
    {
        var zone = ZoneWith(new SrvRecord
        {
            Name   = "_sip._tcp.example.com.",
            Type   = "SRV",
            Ttl    = 600,
            Values = [new SrvValue(10, 20, 65536, "sip.example.com.")]
        });
        var result = ZoneValidator.Validate(zone);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("port") || e.Contains("65536"));
    }

    [Fact]
    public void Validate_SrvNegativePort_ReturnsError()
    {
        var zone = ZoneWith(new SrvRecord
        {
            Name   = "_sip._tcp.example.com.",
            Type   = "SRV",
            Ttl    = 600,
            Values = [new SrvValue(10, 20, -1, "sip.example.com.")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validate_SrvPriorityZero_IsValid()
    {
        var zone = ZoneWith(new SrvRecord
        {
            Name   = "_sip._tcp.example.com.",
            Type   = "SRV",
            Ttl    = 600,
            Values = [new SrvValue(0, 0, 5060, "sip.example.com.")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeTrue();
    }

    // ── MX preference boundaries ──────────────────────────────────────────────

    [Fact]
    public void Validate_MxPreferenceZero_IsValid()
    {
        var zone = ZoneWith(new MxRecord
        {
            Name   = "example.com.",
            Type   = "MX",
            Ttl    = 3600,
            Values = [new MxValue(0, "mail.example.com.")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_MxPreference65535_IsValid()
    {
        var zone = ZoneWith(new MxRecord
        {
            Name   = "example.com.",
            Type   = "MX",
            Ttl    = 3600,
            Values = [new MxValue(65535, "mail.example.com.")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_MxPreferenceNegative_ReturnsError()
    {
        var zone = ZoneWith(new MxRecord
        {
            Name   = "example.com.",
            Type   = "MX",
            Ttl    = 3600,
            Values = [new MxValue(-1, "mail.example.com.")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validate_MxPreferenceExceedsMax_ReturnsError()
    {
        var zone = ZoneWith(new MxRecord
        {
            Name   = "example.com.",
            Type   = "MX",
            Ttl    = 3600,
            Values = [new MxValue(65536, "mail.example.com.")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeFalse();
    }

    // ── CAA flags boundary ────────────────────────────────────────────────────

    [Fact]
    public void Validate_CaaFlagsZero_IsValid()
    {
        var zone = ZoneWith(new CaaRecord
        {
            Name   = "example.com.",
            Type   = "CAA",
            Ttl    = 3600,
            Values = [new CaaValue(0, "issue", "letsencrypt.org")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_CaaFlagsMax_IsValid()
    {
        var zone = ZoneWith(new CaaRecord
        {
            Name   = "example.com.",
            Type   = "CAA",
            Ttl    = 3600,
            Values = [new CaaValue(128, "issue", "letsencrypt.org")]
        });
        ZoneValidator.Validate(zone).IsValid.ShouldBeTrue();
    }
}
