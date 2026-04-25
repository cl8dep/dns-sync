using DnsSync.Core.Records;
using Shouldly;

namespace DnsSync.Tests.Core.Records;

public class RecordNormalizationTests
{
    [Fact]
    public void ARecord_CanonicalHash_IsOrderIndependent()
    {
        var r1 = new ARecord { Name = "x.", Type = "A", Ttl = 300, Addresses = ["1.1.1.1", "2.2.2.2"] };
        var r2 = new ARecord { Name = "x.", Type = "A", Ttl = 300, Addresses = ["2.2.2.2", "1.1.1.1"] };

        r1.CanonicalHash().ShouldBe(r2.CanonicalHash());
    }

    [Fact]
    public void AaaaRecord_CanonicalHash_NormalizesCase()
    {
        var r1 = new AaaaRecord { Name = "x.", Type = "AAAA", Ttl = 300, Addresses = ["2606:4700::1"] };
        var r2 = new AaaaRecord { Name = "x.", Type = "AAAA", Ttl = 300, Addresses = ["2606:4700::1"] };

        r1.CanonicalHash().ShouldBe(r2.CanonicalHash());
    }

    [Fact]
    public void CnameRecord_CanonicalHash_NormalizesTrailingDot()
    {
        var withDot = new CnameRecord { Name = "www.", Type = "CNAME", Ttl = 300, Target = "example.com." };
        var withoutDot = new CnameRecord { Name = "www.", Type = "CNAME", Ttl = 300, Target = "example.com" };

        withDot.CanonicalHash().ShouldBe(withoutDot.CanonicalHash());
    }

    [Fact]
    public void MxRecord_CanonicalHash_IsOrderedByPreference()
    {
        var r1 = new MxRecord
        {
            Name = "x.",
            Type = "MX",
            Ttl = 300,
            Values = [new MxValue(10, "mx1.example.com."), new MxValue(20, "mx2.example.com.")]
        };
        var r2 = new MxRecord
        {
            Name = "x.",
            Type = "MX",
            Ttl = 300,
            Values = [new MxValue(20, "mx2.example.com."), new MxValue(10, "mx1.example.com.")]
        };

        r1.CanonicalHash().ShouldBe(r2.CanonicalHash());
    }

    [Fact]
    public void MxRecord_CanonicalHash_NormalizesExchangeTrailingDot()
    {
        var withDot = new MxRecord
        {
            Name = "x.",
            Type = "MX",
            Ttl = 300,
            Values = [new MxValue(10, "mx1.example.com.")]
        };
        var withoutDot = new MxRecord
        {
            Name = "x.",
            Type = "MX",
            Ttl = 300,
            Values = [new MxValue(10, "mx1.example.com")]
        };

        withDot.CanonicalHash().ShouldBe(withoutDot.CanonicalHash());
    }

    [Fact]
    public void TxtRecord_CanonicalHash_IsOrderIndependent()
    {
        var r1 = new TxtRecord
        {
            Name = "x.",
            Type = "TXT",
            Ttl = 300,
            Values = ["v=spf1 include:_spf.google.com ~all", "google-site-verification=abc"]
        };
        var r2 = new TxtRecord
        {
            Name = "x.",
            Type = "TXT",
            Ttl = 300,
            Values = ["google-site-verification=abc", "v=spf1 include:_spf.google.com ~all"]
        };

        r1.CanonicalHash().ShouldBe(r2.CanonicalHash());
    }

    [Fact]
    public void NsRecord_CanonicalHash_NormalizesTrailingDot()
    {
        var withDot = new NsRecord
        {
            Name = "x.",
            Type = "NS",
            Ttl = 3600,
            Nameservers = ["ns1.example.com.", "ns2.example.com."]
        };
        var withoutDot = new NsRecord
        {
            Name = "x.",
            Type = "NS",
            Ttl = 3600,
            Nameservers = ["ns1.example.com", "ns2.example.com"]
        };

        withDot.CanonicalHash().ShouldBe(withoutDot.CanonicalHash());
    }

    [Fact]
    public void CaaRecord_CanonicalHash_IsOrderIndependent()
    {
        var r1 = new CaaRecord
        {
            Name = "x.",
            Type = "CAA",
            Ttl = 3600,
            Values = [new CaaValue(0, "issue", "letsencrypt.org"), new CaaValue(0, "issuewild", ";")]
        };
        var r2 = new CaaRecord
        {
            Name = "x.",
            Type = "CAA",
            Ttl = 3600,
            Values = [new CaaValue(0, "issuewild", ";"), new CaaValue(0, "issue", "letsencrypt.org")]
        };

        r1.CanonicalHash().ShouldBe(r2.CanonicalHash());
    }
}
