using DnsSync.Core.Records;
using DnsSync.Providers.Yaml;
using Shouldly;

namespace DnsSync.Tests.Providers.Yaml;

public class YamlProviderTests
{
    [Fact]
    public void ParseZoneYaml_ApexARecord_ScalarForm()
    {
        var yaml = """
            '':
              type: A
              ttl: 3600
              values:
                - 1.2.3.4
                - 5.6.7.8
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records.Count.ShouldBe(1);
        var a = records[0].ShouldBeOfType<ARecord>();
        a.Name.ShouldBe("example.com.");
        a.Ttl.ShouldBe(3600);
        a.Addresses.ShouldBe(["1.2.3.4", "5.6.7.8"], ignoreOrder: true);
    }

    [Fact]
    public void ParseZoneYaml_SubdomainCname()
    {
        var yaml = """
            www:
              type: CNAME
              ttl: 300
              value: example.com.
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records.Count.ShouldBe(1);
        var c = records[0].ShouldBeOfType<CnameRecord>();
        c.Name.ShouldBe("www.example.com.");
        c.Target.ShouldBe("example.com.");
    }

    [Fact]
    public void ParseZoneYaml_MxRecord()
    {
        var yaml = """
            '':
              type: MX
              ttl: 600
              values:
                - preference: 10
                  exchange: mx1.example.com.
                - preference: 20
                  exchange: mx2.example.com.
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records.Count.ShouldBe(1);
        var mx = records[0].ShouldBeOfType<MxRecord>();
        mx.Values.Count.ShouldBe(2);
        mx.Values[0].Preference.ShouldBe(10);
        mx.Values[0].Exchange.ShouldBe("mx1.example.com.");
    }

    [Fact]
    public void ParseZoneYaml_TxtRecord()
    {
        var yaml = """
            _dmarc:
              type: TXT
              ttl: 3600
              value: "v=DMARC1; p=reject"
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records.Count.ShouldBe(1);
        var txt = records[0].ShouldBeOfType<TxtRecord>();
        txt.Name.ShouldBe("_dmarc.example.com.");
        txt.Values.ShouldHaveSingleItem().ShouldBe("v=DMARC1; p=reject");
    }

    [Fact]
    public void ParseZoneYaml_ListForm_MultipleRecordsAtSameName()
    {
        var yaml = """
            '':
              - type: A
                ttl: 3600
                values:
                  - 1.2.3.4
              - type: MX
                ttl: 600
                values:
                  - preference: 10
                    exchange: mail.example.com.
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records.Count.ShouldBe(2);
        records.ShouldContain(r => r is ARecord);
        records.ShouldContain(r => r is MxRecord);
    }

    [Fact]
    public void ParseZoneYaml_CaaRecord()
    {
        var yaml = """
            '':
              type: CAA
              ttl: 3600
              values:
                - flags: 0
                  tag: issue
                  value: "letsencrypt.org"
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records.Count.ShouldBe(1);
        var caa = records[0].ShouldBeOfType<CaaRecord>();
        caa.Values[0].Tag.ShouldBe("issue");
        caa.Values[0].Value.ShouldBe("letsencrypt.org");
    }

    [Fact]
    public void ParseZoneYaml_AtSignIsApex()
    {
        var yaml = """
            '@':
              type: A
              ttl: 3600
              values: [1.2.3.4]
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records[0].Name.ShouldBe("example.com.");
    }

    [Fact]
    public void ParseZoneYaml_UnknownRecordType_IsSkipped()
    {
        var yaml = """
            www:
              type: SSHFP
              ttl: 3600
              value: "some value"
            a:
              type: A
              ttl: 300
              values: [1.2.3.4]
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records.Count.ShouldBe(1);
        records[0].Type.ShouldBe("A");
    }

    [Fact]
    public void ParseZoneYaml_AbsoluteFqdnSubdomain_PreservedAsIs()
    {
        var yaml = """
            'api.example.com.':
              type: A
              ttl: 300
              values: [1.2.3.4]
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");

        records.Count.ShouldBe(1);
        records[0].Name.ShouldBe("api.example.com.");  // not api.example.com..example.com.
    }
}
