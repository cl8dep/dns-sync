using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.Yaml;
using Shouldly;

namespace DnsSync.Tests.Providers.Yaml;

public class ZoneYamlSerializerTests
{
    private static DnsZone MakeZone(params DnsRecord[] records) => new()
    {
        Name = "example.com.",
        Records = records
    };

    [Fact]
    public void Serialize_ARecord_RoundTrips()
    {
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = ["1.2.3.4", "5.6.7.8"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        var record = parsed.OfType<ARecord>().Single();
        record.Name.ShouldBe("www.example.com.");
        record.Ttl.ShouldBe(300);
        record.Addresses.ShouldBe(["1.2.3.4", "5.6.7.8"]);
    }

    [Fact]
    public void Serialize_ApexRecord_UsesEmptyKey()
    {
        var zone = MakeZone(new ARecord
        {
            Name = "example.com.",
            Type = "A",
            Ttl = 3600,
            Addresses = ["1.2.3.4"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        yaml.ShouldContain("'':");

        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        parsed.OfType<ARecord>().Single().Name.ShouldBe("example.com.");
    }

    [Fact]
    public void Serialize_CnameRecord_RoundTrips()
    {
        var zone = MakeZone(new CnameRecord
        {
            Name = "blog.example.com.",
            Type = "CNAME",
            Ttl = 600,
            Target = "example.com."
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        var record = parsed.OfType<CnameRecord>().Single();
        record.Target.ShouldBe("example.com.");
    }

    [Fact]
    public void Serialize_MxRecord_RoundTrips()
    {
        var zone = MakeZone(new MxRecord
        {
            Name = "example.com.",
            Type = "MX",
            Ttl = 3600,
            Values =
            [
                new MxValue(10, "mail.example.com."),
                new MxValue(20, "mail2.example.com.")
            ]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        var record = parsed.OfType<MxRecord>().Single();
        record.Values.Count.ShouldBe(2);
        record.Values[0].Preference.ShouldBe(10);
        record.Values[0].Exchange.ShouldBe("mail.example.com.");
        record.Values[1].Preference.ShouldBe(20);
    }

    [Fact]
    public void Serialize_TxtRecord_QuotesValuesWithSpaces()
    {
        var zone = MakeZone(new TxtRecord
        {
            Name = "example.com.",
            Type = "TXT",
            Ttl = 600,
            Values = ["v=spf1 include:_spf.google.com ~all"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        yaml.ShouldContain("\"v=spf1");

        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        parsed.OfType<TxtRecord>().Single().Values[0].ShouldBe("v=spf1 include:_spf.google.com ~all");
    }

    [Fact]
    public void Serialize_TxtRecord_MultiplValues_RoundTrips()
    {
        var zone = MakeZone(new TxtRecord
        {
            Name = "example.com.",
            Type = "TXT",
            Ttl = 600,
            Values = ["v=spf1 ~all", "google-site-verification=abc123"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        var record = parsed.OfType<TxtRecord>().Single();
        record.Values.Count.ShouldBe(2);
        record.Values.ShouldContain("v=spf1 ~all");
        record.Values.ShouldContain("google-site-verification=abc123");
    }

    [Fact]
    public void Serialize_NsRecord_RoundTrips()
    {
        var zone = MakeZone(new NsRecord
        {
            Name = "example.com.",
            Type = "NS",
            Ttl = 3600,
            Nameservers = ["ns1.porkbun.com.", "ns2.porkbun.com."]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        var record = parsed.OfType<NsRecord>().Single();
        record.Nameservers.ShouldContain("ns1.porkbun.com.");
        record.Nameservers.ShouldContain("ns2.porkbun.com.");
    }

    [Fact]
    public void Serialize_CaaRecord_RoundTrips()
    {
        var zone = MakeZone(new CaaRecord
        {
            Name = "example.com.",
            Type = "CAA",
            Ttl = 3600,
            Values = [new CaaValue(0, "issue", "letsencrypt.org")]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        var record = parsed.OfType<CaaRecord>().Single();
        record.Values[0].Flags.ShouldBe(0);
        record.Values[0].Tag.ShouldBe("issue");
        record.Values[0].Value.ShouldBe("letsencrypt.org");
    }

    [Fact]
    public void Serialize_SrvRecord_RoundTrips()
    {
        var zone = MakeZone(new SrvRecord
        {
            Name = "_sip._tcp.example.com.",
            Type = "SRV",
            Ttl = 600,
            Values = [new SrvValue(10, 20, 5060, "sip.example.com.")]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        var record = parsed.OfType<SrvRecord>().Single();
        record.Values[0].Priority.ShouldBe(10);
        record.Values[0].Weight.ShouldBe(20);
        record.Values[0].Port.ShouldBe(5060);
        record.Values[0].Target.ShouldBe("sip.example.com.");
    }

    [Fact]
    public void Serialize_MultipleRecordsSameSubdomain_WritesAsList()
    {
        var zone = MakeZone(
            new ARecord { Name = "example.com.", Type = "A", Ttl = 3600, Addresses = ["1.2.3.4"] },
            new MxRecord
            {
                Name = "example.com.",
                Type = "MX",
                Ttl = 3600,
                Values = [new MxValue(10, "mail.example.com.")]
            });

        var yaml = ZoneYamlSerializer.Serialize(zone);

        var parsed = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        parsed.OfType<ARecord>().ShouldHaveSingleItem();
        parsed.OfType<MxRecord>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Serialize_ApexAppearsFirst()
    {
        var zone = MakeZone(
            new CnameRecord { Name = "www.example.com.", Type = "CNAME", Ttl = 300, Target = "example.com." },
            new ARecord { Name = "example.com.", Type = "A", Ttl = 3600, Addresses = ["1.2.3.4"] });

        var yaml = ZoneYamlSerializer.Serialize(zone);

        var apexPos = yaml.IndexOf("'':", StringComparison.Ordinal);
        var wwwPos = yaml.IndexOf("www:", StringComparison.Ordinal);
        apexPos.ShouldBeLessThan(wwwPos);
    }
}
