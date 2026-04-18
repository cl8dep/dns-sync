using System.Text.Json;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.Porkbun;
using Shouldly;

namespace DnsSync.Tests.Providers.Porkbun;

public class PorkbunParsingTests
{
    private static JsonElement MakeRecord(string type, string name, string content,
        string ttl = "600", string? prio = null)
    {
        var obj = new Dictionary<string, object?>
        {
            ["id"] = "123",
            ["type"] = type,
            ["name"] = name,
            ["content"] = content,
            ["ttl"] = ttl,
            ["prio"] = prio,
            ["notes"] = ""
        };
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj));
    }

    private const string Zone = "example.com.";

    [Fact]
    public void ParsePorkbunRecord_ARecord_ParsedCorrectly()
    {
        var el = MakeRecord("A", "www", "1.2.3.4");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as ARecord;
        record.ShouldNotBeNull();
        record.Name.ShouldBe("www.example.com.");
        record.Addresses.ShouldBe(["1.2.3.4"]);
        record.Ttl.ShouldBe(600);
    }

    [Fact]
    public void ParsePorkbunRecord_ApexRecord_FullZoneName()
    {
        var el = MakeRecord("A", "example.com", "1.2.3.4");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as ARecord;
        record.ShouldNotBeNull();
        record.Name.ShouldBe("example.com.");
    }

    [Fact]
    public void ParsePorkbunRecord_AaaaRecord_ParsedCorrectly()
    {
        var el = MakeRecord("AAAA", "ipv6", "2001:db8::1");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as AaaaRecord;
        record.ShouldNotBeNull();
        record.Addresses.ShouldBe(["2001:db8::1"]);
    }

    [Fact]
    public void ParsePorkbunRecord_CnameRecord_NormalizesFqdn()
    {
        var el = MakeRecord("CNAME", "www", "example.com");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as CnameRecord;
        record.ShouldNotBeNull();
        record.Target.ShouldBe("example.com.");
    }

    [Fact]
    public void ParsePorkbunRecord_AliasRecord_ParsedAsCname()
    {
        var el = MakeRecord("ALIAS", "www", "example.com");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as CnameRecord;
        record.ShouldNotBeNull();
        record.Type.ShouldBe("CNAME");
    }

    [Fact]
    public void ParsePorkbunRecord_MxRecord_ParsedWithPriority()
    {
        var el = MakeRecord("MX", "example.com", "mail.example.com", prio: "10");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as MxRecord;
        record.ShouldNotBeNull();
        record.Values.Count.ShouldBe(1);
        record.Values[0].Preference.ShouldBe(10);
        record.Values[0].Exchange.ShouldBe("mail.example.com.");
    }

    [Fact]
    public void ParsePorkbunRecord_TxtRecord_StripsOuterQuotes()
    {
        var el = MakeRecord("TXT", "example.com", "\"v=spf1 ~all\"");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as TxtRecord;
        record.ShouldNotBeNull();
        record.Values[0].ShouldBe("v=spf1 ~all");
    }

    [Fact]
    public void ParsePorkbunRecord_TxtRecord_UnquotedContentPreserved()
    {
        var el = MakeRecord("TXT", "example.com", "v=spf1 ~all");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as TxtRecord;
        record.ShouldNotBeNull();
        record.Values[0].ShouldBe("v=spf1 ~all");
    }

    [Fact]
    public void ParsePorkbunRecord_NsRecord_NormalizesFqdn()
    {
        var el = MakeRecord("NS", "example.com", "ns1.porkbun.com");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as NsRecord;
        record.ShouldNotBeNull();
        record.Nameservers[0].ShouldBe("ns1.porkbun.com.");
    }

    [Fact]
    public void ParsePorkbunRecord_CaaRecord_ParsedCorrectly()
    {
        var el = MakeRecord("CAA", "example.com", "0 issue \"letsencrypt.org\"");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as CaaRecord;
        record.ShouldNotBeNull();
        record.Values[0].Flags.ShouldBe(0);
        record.Values[0].Tag.ShouldBe("issue");
        record.Values[0].Value.ShouldBe("letsencrypt.org");
    }

    [Fact]
    public void ParsePorkbunRecord_SrvRecord_ParsedCorrectly()
    {
        var el = MakeRecord("SRV", "_sip._tcp.example.com", "20 5060 sip.example.com", prio: "10");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone) as SrvRecord;
        record.ShouldNotBeNull();
        record.Values[0].Priority.ShouldBe(10);
        record.Values[0].Weight.ShouldBe(20);
        record.Values[0].Port.ShouldBe(5060);
        record.Values[0].Target.ShouldBe("sip.example.com.");
    }

    [Fact]
    public void ParsePorkbunRecord_UnknownType_ReturnsNull()
    {
        var el = MakeRecord("TLSA", "example.com", "0 0 1 abc123");
        var record = PorkbunProvider.ParsePorkbunRecord(el, Zone);
        record.ShouldBeNull();
    }
}
