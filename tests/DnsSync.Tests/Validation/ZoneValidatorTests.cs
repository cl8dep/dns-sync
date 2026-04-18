using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Validation;
using Shouldly;

namespace DnsSync.Tests.Validation;

public class ZoneValidatorTests
{
    private static DnsZone ZoneWith(params DnsRecord[] records) =>
        new() { Name = "example.com.", Records = records };

    [Fact]
    public void Validate_ValidARecord_IsValid()
    {
        var zone = ZoneWith(new ARecord
        {
            Name = "www.example.com.", Type = "A", Ttl = 300,
            Addresses = ["1.2.3.4"]
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NegativeTtl_ReturnsError()
    {
        var zone = ZoneWith(new ARecord
        {
            Name = "www.example.com.", Type = "A", Ttl = -1,
            Addresses = ["1.2.3.4"]
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("TTL"));
    }

    [Fact]
    public void Validate_LowTtl_ReturnsWarning()
    {
        var zone = ZoneWith(new ARecord
        {
            Name = "www.example.com.", Type = "A", Ttl = 1,
            Addresses = ["1.2.3.4"]
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("low TTL"));
    }

    [Fact]
    public void Validate_CnameWithOtherRecord_ReturnsError()
    {
        var zone = ZoneWith(
            new CnameRecord { Name = "www.example.com.", Type = "CNAME", Ttl = 300, Target = "example.com." },
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }
        );

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("CNAME") && e.Contains("coexist"));
    }

    [Fact]
    public void Validate_EmptyAddresses_ReturnsError()
    {
        var zone = ZoneWith(new ARecord
        {
            Name = "www.example.com.", Type = "A", Ttl = 300,
            Addresses = []
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("A") && e.Contains("no addresses"));
    }

    [Fact]
    public void Validate_EmptyMxValues_ReturnsError()
    {
        var zone = ZoneWith(new MxRecord
        {
            Name = "example.com.", Type = "MX", Ttl = 600,
            Values = []
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("MX") && e.Contains("no values"));
    }

    [Fact]
    public void Validate_RecordWithoutTrailingDot_ReturnsError()
    {
        var zone = new DnsZone
        {
            Name = "example.com.",
            Records =
            [
                new ARecord { Name = "www.example.com", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }
            ]
        };

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("FQDN") && e.Contains("trailing dot"));
    }

    [Fact]
    public void Validate_InvalidIpv4_ReturnsError()
    {
        var zone = ZoneWith(new ARecord
        {
            Name = "www.example.com.", Type = "A", Ttl = 300,
            Addresses = ["not-an-ip"]
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("not-an-ip") && e.Contains("IPv4"));
    }

    [Fact]
    public void Validate_InvalidIpv6_ReturnsError()
    {
        var zone = ZoneWith(new AaaaRecord
        {
            Name = "www.example.com.", Type = "AAAA", Ttl = 300,
            Addresses = ["192.168.1.1"]  // IPv4 address in AAAA record
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("192.168.1.1") && e.Contains("IPv6"));
    }

    [Fact]
    public void Validate_CnameWithInvalidTarget_ReturnsError()
    {
        var zone = ZoneWith(new CnameRecord
        {
            Name = "www.example.com.", Type = "CNAME", Ttl = 300,
            Target = "not a valid hostname!!"
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("CNAME") && e.Contains("valid hostname"));
    }

    [Fact]
    public void Validate_ValidIpv6_IsValid()
    {
        var zone = ZoneWith(new AaaaRecord
        {
            Name = "www.example.com.", Type = "AAAA", Ttl = 300,
            Addresses = ["2001:db8::1"]
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_SrvPortOutOfRange_ReturnsError()
    {
        var zone = ZoneWith(new SrvRecord
        {
            Name = "_sip._tcp.example.com.", Type = "SRV", Ttl = 300,
            Values = [new SrvValue(10, 20, 99999, "sip.example.com.")]
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("SRV") && e.Contains("port") && e.Contains("out of range"));
    }

    [Fact]
    public void Validate_SrvPortZero_IsValid()
    {
        var zone = ZoneWith(new SrvRecord
        {
            Name = "_sip._tcp.example.com.", Type = "SRV", Ttl = 300,
            Values = [new SrvValue(10, 20, 0, "sip.example.com.")]
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_MxNoValues_ReturnsError()
    {
        var zone = ZoneWith(new MxRecord
        {
            Name = "example.com.", Type = "MX", Ttl = 300,
            Values = []
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("MX") && e.Contains("no values"));
    }

    [Fact]
    public void Validate_NsNoNameservers_ReturnsError()
    {
        var zone = ZoneWith(new NsRecord
        {
            Name = "example.com.", Type = "NS", Ttl = 300,
            Nameservers = []
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("NS") && e.Contains("no nameservers"));
    }

    [Fact]
    public void Validate_CaaNoValues_ReturnsError()
    {
        var zone = ZoneWith(new CaaRecord
        {
            Name = "example.com.", Type = "CAA", Ttl = 300,
            Values = []
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("CAA") && e.Contains("no values"));
    }

    [Fact]
    public void Validate_SrvNegativePort_ReturnsError()
    {
        var zone = ZoneWith(new SrvRecord
        {
            Name = "_sip._tcp.example.com.", Type = "SRV", Ttl = 300,
            Values = [new SrvValue(10, 20, -1, "sip.example.com.")]
        });

        var result = ZoneValidator.Validate(zone);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("SRV") && e.Contains("port") && e.Contains("out of range"));
    }
}
