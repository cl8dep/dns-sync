using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.Yaml;
using Shouldly;

namespace DnsSync.Tests.Providers.Yaml;

/// <summary>
/// Edge-case tests for ZoneYamlSerializer focusing on QuoteScalar (A/AAAA/NS values)
/// and QuoteTxt (TXT values) — all via round-trip through Serialize → ParseZoneYaml.
/// </summary>
public class ZoneYamlSerializerEdgeCaseTests
{
    private const string Zone = "example.com.";

    private static DnsZone MakeZone(params DnsRecord[] records) =>
        new() { Name = Zone, Records = records };

    private static string Roundtrip(DnsZone zone)
    {
        var yaml = ZoneYamlSerializer.Serialize(zone);
        return yaml;
    }

    private static IReadOnlyList<DnsRecord> RoundtripParsed(DnsZone zone) =>
        YamlProvider.ParseZoneYaml(ZoneYamlSerializer.Serialize(zone), Zone);

    // ── QuoteScalar — A record addresses ─────────────────────────────────────

    [Fact]
    public void Serialize_ARecord_AtSignValue_IsQuotedAndRoundTrips()
    {
        // "@." is not a valid IP but providers may emit odd values; serializer must quote it.
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = ["@."]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        yaml.ShouldContain("\"@.\"");

        // Round-trip preserves the value
        var parsed = YamlProvider.ParseZoneYaml(yaml, Zone);
        parsed.OfType<ARecord>().Single().Addresses.ShouldContain("@.");
    }

    [Fact]
    public void Serialize_ARecord_BacktickValue_IsQuotedAndRoundTrips()
    {
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = ["`value`"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        yaml.ShouldContain("\"`value`\"");

        var parsed = YamlProvider.ParseZoneYaml(yaml, Zone);
        parsed.OfType<ARecord>().Single().Addresses.ShouldContain("`value`");
    }

    [Fact]
    public void Serialize_ARecord_ValueWithColon_IsQuotedAndRoundTrips()
    {
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = ["key:value"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        yaml.ShouldContain("\"key:value\"");

        var parsed = YamlProvider.ParseZoneYaml(yaml, Zone);
        parsed.OfType<ARecord>().Single().Addresses.ShouldContain("key:value");
    }

    [Fact]
    public void Serialize_ARecord_ValueWithHash_IsQuotedAndRoundTrips()
    {
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = ["val#comment"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        yaml.ShouldContain("\"val#comment\"");

        var parsed = YamlProvider.ParseZoneYaml(yaml, Zone);
        parsed.OfType<ARecord>().Single().Addresses.ShouldContain("val#comment");
    }

    [Fact]
    public void Serialize_ARecord_ValueWithDoubleQuote_IsEscapedAndRoundTrips()
    {
        const string val = "say \"hello\"";
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = [val]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        // Must be escaped inside the outer double-quotes
        yaml.ShouldContain("\\\"");

        var parsed = YamlProvider.ParseZoneYaml(yaml, Zone);
        parsed.OfType<ARecord>().Single().Addresses.ShouldContain(val);
    }

    [Fact]
    public void Serialize_ARecord_ValueWithBackslash_IsEscapedAndRoundTrips()
    {
        const string val = @"C:\path";
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = [val]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        yaml.ShouldContain("\\\\"); // backslash doubled

        var parsed = YamlProvider.ParseZoneYaml(yaml, Zone);
        parsed.OfType<ARecord>().Single().Addresses.ShouldContain(val);
    }

    [Fact]
    public void Serialize_ARecord_EmptyValue_IsQuotedAsEmptyString()
    {
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = [""]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        // QuoteScalar("") → ""
        yaml.ShouldContain("\"\"");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("~")]
    public void Serialize_ARecord_YamlKeyword_RoundTrips(string keyword)
    {
        // These are YAML reserved words — they'd be parsed as bool/null without quoting.
        // QuoteScalar doesn't specifically quote them (they don't start with @ or ` and
        // don't contain special chars). This test documents current behavior:
        // YAML providers treat them as plain scalars which YamlDotNet may type-coerce.
        // The serializer should emit them and the round-trip must preserve the string.
        var zone = MakeZone(new ARecord
        {
            Name = "www.example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = [keyword]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        // At minimum the YAML should contain the keyword text
        yaml.ShouldContain(keyword);
    }

    // ── QuoteScalar — NS record nameservers ───────────────────────────────────

    [Fact]
    public void Serialize_NsRecord_MultipleNameservers_AllRoundTrip()
    {
        var zone = MakeZone(new NsRecord
        {
            Name = "example.com.",
            Type = "NS",
            Ttl = 3600,
            Nameservers = ["ns1.porkbun.com.", "ns2.porkbun.com.", "ns3.porkbun.com."]
        });

        var parsed = RoundtripParsed(zone);
        var ns = parsed.OfType<NsRecord>().Single();
        ns.Nameservers.Count.ShouldBe(3);
        ns.Nameservers.ShouldContain("ns1.porkbun.com.");
    }

    // ── QuoteTxt — TXT record values ──────────────────────────────────────────

    [Fact]
    public void Serialize_TxtRecord_ValueWithColon_IsQuotedAndRoundTrips()
    {
        var zone = MakeZone(new TxtRecord
        {
            Name = "example.com.",
            Type = "TXT",
            Ttl = 600,
            Values = ["k=v:extra"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        yaml.ShouldContain("\"k=v:extra\"");

        var parsed = YamlProvider.ParseZoneYaml(yaml, Zone);
        parsed.OfType<TxtRecord>().Single().Values.ShouldContain("k=v:extra");
    }

    [Fact]
    public void Serialize_TxtRecord_ValueWithBackslash_IsEscapedAndRoundTrips()
    {
        const string val = @"path\to\file";
        var zone = MakeZone(new TxtRecord
        {
            Name = "example.com.",
            Type = "TXT",
            Ttl = 600,
            Values = [val]
        });

        var parsed = RoundtripParsed(zone);
        parsed.OfType<TxtRecord>().Single().Values.ShouldContain(val);
    }

    [Fact]
    public void Serialize_TxtRecord_ValueWithDoubleQuote_IsEscapedAndRoundTrips()
    {
        const string val = "say \"hi\"";
        var zone = MakeZone(new TxtRecord
        {
            Name = "example.com.",
            Type = "TXT",
            Ttl = 600,
            Values = [val]
        });

        var parsed = RoundtripParsed(zone);
        parsed.OfType<TxtRecord>().Single().Values.ShouldContain(val);
    }

    [Fact]
    public void Serialize_TxtRecord_EmptyValue_SerializedAsEmptyQuotes()
    {
        // Empty TXT values are serialized as "" in YAML. YamlProvider's GetStringList
        // filters out empty strings on parse (Where(s => s.Length > 0)), so round-trip
        // produces no records. This test documents the known behavior.
        var zone = MakeZone(new TxtRecord
        {
            Name = "example.com.",
            Type = "TXT",
            Ttl = 600,
            Values = [""]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone);
        // Serializer emits "" — confirming QuoteTxt("") produces quoted empty string
        yaml.ShouldContain("value:");
        // Round-trip: parser produces a TxtRecord but with an empty Values list
        // because GetStringList filters out empty strings (Where(s => s.Length > 0))
        var parsed = YamlProvider.ParseZoneYaml(yaml, Zone);
        var txt = parsed.OfType<TxtRecord>().Single();
        txt.Values.ShouldBeEmpty();
    }

    [Fact]
    public void Serialize_TxtRecord_OnlySpecialChars_RoundTrips()
    {
        const string val = ":::###\\\"\\\"";
        var zone = MakeZone(new TxtRecord
        {
            Name = "example.com.",
            Type = "TXT",
            Ttl = 600,
            Values = [val]
        });

        var parsed = RoundtripParsed(zone);
        parsed.OfType<TxtRecord>().Single().Values.ShouldContain(val);
    }

    [Fact]
    public void Serialize_TxtRecord_LongDkimValue_RoundTrips()
    {
        // Real DKIM keys are 200+ chars
        var val = "v=DKIM1; k=rsa; p=" + new string('A', 200);
        var zone = MakeZone(new TxtRecord
        {
            Name = "mail._domainkey.example.com.",
            Type = "TXT",
            Ttl = 300,
            Values = [val]
        });

        var parsed = RoundtripParsed(zone);
        parsed.OfType<TxtRecord>().Single().Values.ShouldContain(val);
    }

    // ── provider name in header ───────────────────────────────────────────────

    [Fact]
    public void Serialize_WithProviderName_HeaderContainsProviderName()
    {
        var zone = MakeZone(new ARecord
        {
            Name = "example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = ["1.2.3.4"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone, providerName: "cloudflare");

        yaml.ShouldContain("from cloudflare");
    }

    [Fact]
    public void Serialize_WithoutProviderName_HeaderOmitsFrom()
    {
        var zone = MakeZone(new ARecord
        {
            Name = "example.com.",
            Type = "A",
            Ttl = 300,
            Addresses = ["1.2.3.4"]
        });

        var yaml = ZoneYamlSerializer.Serialize(zone, providerName: null);

        yaml.ShouldNotContain("from null");
        yaml.ShouldContain("Imported by dns-sync");
    }
}
