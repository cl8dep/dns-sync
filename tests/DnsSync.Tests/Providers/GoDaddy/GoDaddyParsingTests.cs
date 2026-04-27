using System.Text.Json;
using DnsSync.Core.Records;
using DnsSync.Providers.GoDaddy;
using Shouldly;

namespace DnsSync.Tests.Providers.GoDaddy;

public class GoDaddyParsingTests
{
    private static JsonElement Record(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseRecord_ARecord_ParsesCorrectly()
    {
        var r = Record("""{"type":"A","name":"www","data":"1.2.3.4","ttl":3600}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var a = record.ShouldBeOfType<ARecord>();
        a.Name.ShouldBe("www.example.com.");
        a.Ttl.ShouldBe(3600);
        a.Addresses.ShouldBe(["1.2.3.4"]);
    }

    [Fact]
    public void ParseRecord_ApexRecord_UsesZoneName()
    {
        var r = Record("""{"type":"A","name":"@","data":"5.6.7.8","ttl":300}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var a = record.ShouldBeOfType<ARecord>();
        a.Name.ShouldBe("example.com.");
    }

    [Fact]
    public void ParseRecord_AaaaRecord_ParsesCorrectly()
    {
        var r = Record("""{"type":"AAAA","name":"ipv6","data":"2001:db8::1","ttl":600}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var aaaa = record.ShouldBeOfType<AaaaRecord>();
        aaaa.Name.ShouldBe("ipv6.example.com.");
        aaaa.Addresses.ShouldBe(["2001:db8::1"]);
    }

    [Fact]
    public void ParseRecord_CnameRecord_ParsesCorrectly()
    {
        var r = Record("""{"type":"CNAME","name":"www","data":"example.com","ttl":300}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var cname = record.ShouldBeOfType<CnameRecord>();
        cname.Name.ShouldBe("www.example.com.");
        cname.Target.ShouldBe("example.com.");
    }

    [Fact]
    public void ParseRecord_MxRecord_ParsesCorrectly()
    {
        var r = Record("""{"type":"MX","name":"@","data":"mail.example.com","ttl":600,"priority":10}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var mx = record.ShouldBeOfType<MxRecord>();
        mx.Name.ShouldBe("example.com.");
        mx.Values.Count.ShouldBe(1);
        mx.Values[0].Preference.ShouldBe(10);
        mx.Values[0].Exchange.ShouldBe("mail.example.com.");
    }

    [Fact]
    public void ParseRecord_TxtRecord_ParsesCorrectly()
    {
        var r = Record("""{"type":"TXT","name":"@","data":"v=spf1 include:_spf.google.com ~all","ttl":3600}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var txt = record.ShouldBeOfType<TxtRecord>();
        txt.Values.ShouldHaveSingleItem().ShouldBe("v=spf1 include:_spf.google.com ~all");
    }

    [Fact]
    public void ParseRecord_TxtRecord_QuotedValue_UnquotedCorrectly()
    {
        var r = Record("""{"type":"TXT","name":"@","data":"\"v=DMARC1; p=reject\"","ttl":3600}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var txt = record.ShouldBeOfType<TxtRecord>();
        txt.Values.ShouldHaveSingleItem().ShouldBe("v=DMARC1; p=reject");
    }

    [Fact]
    public void ParseRecord_NsRecord_ParsesCorrectly()
    {
        var r = Record("""{"type":"NS","name":"@","data":"ns1.godaddy.com","ttl":3600}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var ns = record.ShouldBeOfType<NsRecord>();
        ns.Nameservers.ShouldHaveSingleItem().ShouldBe("ns1.godaddy.com.");
    }

    [Fact]
    public void ParseRecord_CaaRecord_ParsesCorrectly()
    {
        var r = Record("""{"type":"CAA","name":"@","data":"0 issue \"letsencrypt.org\"","ttl":3600}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        var caa = record.ShouldBeOfType<CaaRecord>();
        caa.Values.Count.ShouldBe(1);
        caa.Values[0].Flags.ShouldBe(0);
        caa.Values[0].Tag.ShouldBe("issue");
        caa.Values[0].Value.ShouldBe("letsencrypt.org");
    }

    [Fact]
    public void ParseRecord_UnknownType_ReturnsNull()
    {
        var r = Record("""{"type":"SSHFP","name":"@","data":"some value","ttl":3600}""");
        var record = GoDaddyProvider.ParseRecord(r, "example.com.");

        record.ShouldBeNull();
    }
}
