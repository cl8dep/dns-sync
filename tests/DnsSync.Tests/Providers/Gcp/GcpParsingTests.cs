using DnsSync.Providers.Gcp;
using Shouldly;

namespace DnsSync.Tests.Providers.Gcp;

public class GcpParsingTests
{
    // ─── UnquoteTxt ───────────────────────────────────────────────────────────

    [Fact]
    public void UnquoteTxt_SingleQuotedChunk_ReturnsPlainString()
    {
        GcpCloudDnsProvider.UnquoteTxt("\"v=spf1 include:example.com ~all\"")
            .ShouldBe("v=spf1 include:example.com ~all");
    }

    [Fact]
    public void UnquoteTxt_TwoChunks_JoinsChunks()
    {
        GcpCloudDnsProvider.UnquoteTxt("\"chunk1\" \"chunk2\"")
            .ShouldBe("chunk1chunk2");
    }

    [Fact]
    public void UnquoteTxt_EscapedQuoteInsideChunk_IsPreserved()
    {
        GcpCloudDnsProvider.UnquoteTxt("\"hello \\\"world\\\"\"")
            .ShouldBe("hello \"world\"");
    }

    [Fact]
    public void UnquoteTxt_BackslashEscape_IsHandled()
    {
        GcpCloudDnsProvider.UnquoteTxt("\"back\\\\slash\"").ShouldBe("back\\slash");
    }

    [Fact]
    public void UnquoteTxt_EmptyChunk_ReturnsEmpty()
    {
        GcpCloudDnsProvider.UnquoteTxt("\"\"").ShouldBe("");
    }

    [Fact]
    public void UnquoteTxt_UnquotedValue_ReturnedAsIs()
    {
        GcpCloudDnsProvider.UnquoteTxt("plainvalue").ShouldBe("plainvalue");
    }

    // ─── QuoteTxt ─────────────────────────────────────────────────────────────

    [Fact]
    public void QuoteTxt_ShortValue_WrapsInSingleChunk()
    {
        GcpCloudDnsProvider.QuoteTxt("v=spf1 ~all")
            .ShouldBe("\"v=spf1 ~all\"");
    }

    [Fact]
    public void QuoteTxt_ValueWithQuotes_EscapesQuotes()
    {
        GcpCloudDnsProvider.QuoteTxt("say \"hello\"")
            .ShouldBe("\"say \\\"hello\\\"\"");
    }

    [Fact]
    public void QuoteTxt_RoundTrip_PreservesValue()
    {
        var original = "v=spf1 include:example.com include:other.com ~all";
        var quoted = GcpCloudDnsProvider.QuoteTxt(original);
        GcpCloudDnsProvider.UnquoteTxt(quoted).ShouldBe(original);
    }

    [Fact]
    public void QuoteTxt_LongValue_SplitsInto255ByteChunks()
    {
        var longValue = new string('a', 300);
        var quoted = GcpCloudDnsProvider.QuoteTxt(longValue);
        // Should produce two chunks: "aaa...255" "aaa...45"
        quoted.ShouldContain("\" \"");
        GcpCloudDnsProvider.UnquoteTxt(quoted).ShouldBe(longValue);
    }

    // ─── ParseMxRrdata (via reflection) ──────────────────────────────────────

    private static T InvokeStatic<T>(string methodName, object?[] args)
    {
        var method = typeof(GcpCloudDnsProvider)
            .GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (T)method.Invoke(null, args)!;
    }

    [Fact]
    public void ParseMxRrdata_ValidFormat_ParsesPrefAndExchange()
    {
        var result = InvokeStatic<DnsSync.Core.Records.MxValue>("ParseMxRrdata", ["10 mail.example.com."]);
        result.Preference.ShouldBe(10);
        result.Exchange.ShouldBe("mail.example.com.");
    }

    [Fact]
    public void ParseMxRrdata_NoSpace_DefaultsPreference10()
    {
        var result = InvokeStatic<DnsSync.Core.Records.MxValue>("ParseMxRrdata", ["mail.example.com."]);
        result.Preference.ShouldBe(10);
        result.Exchange.ShouldBe("mail.example.com.");
    }

    [Fact]
    public void ParseCaaRrdata_ValidFormat_ParsesFlagsTagValue()
    {
        var result = InvokeStatic<DnsSync.Core.Records.CaaValue>("ParseCaaRrdata", ["0 issue \"letsencrypt.org\""]);
        result.Flags.ShouldBe(0);
        result.Tag.ShouldBe("issue");
        result.Value.ShouldBe("letsencrypt.org");
    }

    [Fact]
    public void ParseCaaRrdata_UnquotedValue_ParsesCorrectly()
    {
        var result = InvokeStatic<DnsSync.Core.Records.CaaValue>("ParseCaaRrdata", ["128 issuewild letsencrypt.org"]);
        result.Flags.ShouldBe(128);
        result.Tag.ShouldBe("issuewild");
        result.Value.ShouldBe("letsencrypt.org");
    }

    [Fact]
    public void ParseSrvRrdata_ValidFormat_ParsesAllFields()
    {
        var result = InvokeStatic<DnsSync.Core.Records.SrvValue>("ParseSrvRrdata", ["10 20 5060 sip.example.com."]);
        result.Priority.ShouldBe(10);
        result.Weight.ShouldBe(20);
        result.Port.ShouldBe(5060);
        result.Target.ShouldBe("sip.example.com.");
    }

    [Fact]
    public void ParseSrvRrdata_MissingFields_DefaultsToZero()
    {
        var result = InvokeStatic<DnsSync.Core.Records.SrvValue>("ParseSrvRrdata", ["10 20"]);
        result.Priority.ShouldBe(10);
        result.Weight.ShouldBe(20);
        result.Port.ShouldBe(0);
    }
}
