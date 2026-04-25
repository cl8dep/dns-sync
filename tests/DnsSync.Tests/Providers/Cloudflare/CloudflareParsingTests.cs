using System.Text.Json;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.Cloudflare;
using Shouldly;

namespace DnsSync.Tests.Providers.Cloudflare;

public class CloudflareParsingTests
{
    // CloudflareProvider.StripTxtQuotes is private — test via ParseZoneYaml or directly via reflection
    // Instead we test the public behaviour through the internal static helper exposed for tests.
    // We use the public GcpCloudDnsProvider.UnquoteTxt as reference and test Cloudflare
    // parsing by constructing a minimal provider and exercising ParseCloudflareRecord via
    // the existing MergeIntoRRsets path through GetZoneAsync is impractical without HTTP mocks.
    // We therefore test StripTxtQuotes indirectly through the CloudflareProvider's parsing
    // by calling it through the non-public method via reflection.

    private static string InvokeStripTxtQuotes(string input)
    {
        var method = typeof(CloudflareProvider)
            .GetMethod("StripTxtQuotes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, [input])!;
    }

    private static JsonElement MakeRecord(string type, string name, int ttl, string content,
        int? priority = null, string? data = null)
    {
        var obj = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["type"] = type,
            ["name"] = name,
            ["ttl"] = ttl,
            ["content"] = content,
        };
        if (priority.HasValue) obj["priority"] = priority.Value;
        if (data != null)
        {
            // For SRV, data is JSON string that gets re-parsed
            obj["content"] = "0 0 0 .";  // ignored for SRV
            obj["data"] = JsonSerializer.Deserialize<JsonElement>(data);
        }

        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static DnsRecord? ParseRecord(JsonElement el, string zoneName = "example.com.")
    {
        var method = typeof(CloudflareProvider)
            .GetMethod("ParseCloudflareRecord",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // We need a provider instance — create with a dummy token (no HTTP calls made)
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<CloudflareProvider>.Instance;
        var provider = new CloudflareProvider("dummy_token", logger);
        return (DnsRecord?)method.Invoke(provider, [el, zoneName]);
    }

    // ─── StripTxtQuotes ───────────────────────────────────────────────────────

    [Fact]
    public void StripTxtQuotes_SimpleSingleChunk_ReturnsPlainString()
    {
        InvokeStripTxtQuotes("\"v=spf1 include:example.com ~all\"")
            .ShouldBe("v=spf1 include:example.com ~all");
    }

    [Fact]
    public void StripTxtQuotes_TwoChunks_JoinsChunks()
    {
        InvokeStripTxtQuotes("\"chunk1\" \"chunk2\"")
            .ShouldBe("chunk1chunk2");
    }

    [Fact]
    public void StripTxtQuotes_EscapedQuoteInsideChunk_IsPreserved()
    {
        InvokeStripTxtQuotes("\"hello \\\"world\\\"\"")
            .ShouldBe("hello \"world\"");
    }

    [Fact]
    public void StripTxtQuotes_UnquotedSingleToken_ReturnedAsIs()
    {
        InvokeStripTxtQuotes("plainvalue")
            .ShouldBe("plainvalue");
    }

    [Fact]
    public void StripTxtQuotes_EmptyString_ReturnsEmpty()
    {
        InvokeStripTxtQuotes("\"\"").ShouldBe("");
    }

    [Fact]
    public void StripTxtQuotes_BackslashEscape_IsHandled()
    {
        InvokeStripTxtQuotes("\"back\\\\slash\"").ShouldBe("back\\slash");
    }

    // ─── Record parsing ───────────────────────────────────────────────────────

    [Fact]
    public void ParseCloudflareRecord_ARecord_ParsedCorrectly()
    {
        var el = MakeRecord("A", "example.com", 3600, "1.2.3.4");
        var record = ParseRecord(el) as ARecord;
        record.ShouldNotBeNull();
        record.Addresses.ShouldBe(["1.2.3.4"]);
        record.Ttl.ShouldBe(3600);
        record.Name.ShouldBe("example.com.");
    }

    [Fact]
    public void ParseCloudflareRecord_AaaaRecord_ParsedCorrectly()
    {
        var el = MakeRecord("AAAA", "host.example.com", 300, "2001:db8::1");
        var record = ParseRecord(el) as AaaaRecord;
        record.ShouldNotBeNull();
        record.Addresses.ShouldBe(["2001:db8::1"]);
    }

    [Fact]
    public void ParseCloudflareRecord_CnameRecord_NormalizesFqdn()
    {
        var el = MakeRecord("CNAME", "www.example.com", 3600, "example.com");
        var record = ParseRecord(el) as CnameRecord;
        record.ShouldNotBeNull();
        record.Target.ShouldBe("example.com.");
    }

    [Fact]
    public void ParseCloudflareRecord_MxRecord_ParsedWithPriority()
    {
        var el = MakeRecord("MX", "example.com", 3600, "mail.example.com", priority: 10);
        var record = ParseRecord(el) as MxRecord;
        record.ShouldNotBeNull();
        record.Values.Count.ShouldBe(1);
        record.Values[0].Preference.ShouldBe(10);
        record.Values[0].Exchange.ShouldBe("mail.example.com.");
    }

    [Fact]
    public void ParseCloudflareRecord_TxtRecord_StripsQuotes()
    {
        var el = MakeRecord("TXT", "example.com", 3600, "\"v=spf1 ~all\"");
        var record = ParseRecord(el) as TxtRecord;
        record.ShouldNotBeNull();
        record.Values[0].ShouldBe("v=spf1 ~all");
    }

    [Fact]
    public void ParseCloudflareRecord_AutoTtl_NormalizedTo300()
    {
        var el = MakeRecord("A", "example.com", 1, "1.2.3.4");
        var record = ParseRecord(el) as ARecord;
        record.ShouldNotBeNull();
        record.Ttl.ShouldBe(300);
    }

    [Fact]
    public void ParseCloudflareRecord_UnknownType_ReturnsNull()
    {
        var el = MakeRecord("SOA", "example.com", 3600, "ns1 admin 1 2 3 4 5");
        var record = ParseRecord(el);
        record.ShouldBeNull();
    }

    // ─── MergeIntoRRsets ──────────────────────────────────────────────────────

    [Fact]
    public void MergeIntoRRsets_MultipleARecords_MergedToSingleRRset()
    {
        var method = typeof(CloudflareProvider)
            .GetMethod("MergeIntoRRsets",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var flat = new List<DnsRecord>
        {
            new ARecord { Name = "example.com.", Type = "A", Ttl = 3600, Addresses = ["1.2.3.4"] },
            new ARecord { Name = "example.com.", Type = "A", Ttl = 3600, Addresses = ["5.6.7.8"] },
        };

        var result = (IReadOnlyList<DnsRecord>)method.Invoke(null, [flat])!;
        result.Count.ShouldBe(1);
        var merged = result[0] as ARecord;
        merged.ShouldNotBeNull();
        merged.Addresses.ShouldBe(["1.2.3.4", "5.6.7.8"]);
    }
}
