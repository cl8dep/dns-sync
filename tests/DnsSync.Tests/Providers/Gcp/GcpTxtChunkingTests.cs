using DnsSync.Providers.Gcp;
using Shouldly;

namespace DnsSync.Tests.Providers.Gcp;

/// <summary>
/// Tests for GCP TXT record chunking/unchunking.
///
/// GCP Cloud DNS splits TXT values longer than 255 bytes into multiple adjacent
/// quoted strings in a single rrdata entry, e.g.:
///   "first 255 bytes" "remaining bytes"
///
/// UnquoteTxt must reassemble chunks into one plain string.
/// QuoteTxt must re-split long values back into 255-byte chunks so that the
/// deletion request matches the stored format exactly.
/// </summary>
public class GcpTxtChunkingTests
{
    // ─── UnquoteTxt ───────────────────────────────────────────────────────────

    [Fact]
    public void UnquoteTxt_ShortValue_StripsSinglePairOfQuotes()
    {
        var result = GcpCloudDnsProvider.UnquoteTxt("\"v=spf1 include:example.com ~all\"");
        result.ShouldBe("v=spf1 include:example.com ~all");
    }

    [Fact]
    public void UnquoteTxt_TwoChunks_ConcatenatesContent()
    {
        // This is the exact format GCP returns for long DKIM records.
        var chunk1 = new string('A', 255);
        var chunk2 = "Lk6jQIDAQAB";
        var rrdata = $"\"{chunk1}\" \"{chunk2}\"";

        var result = GcpCloudDnsProvider.UnquoteTxt(rrdata);

        result.ShouldBe(chunk1 + chunk2);
    }

    [Fact]
    public void UnquoteTxt_ThreeChunks_ConcatenatesAll()
    {
        var rrdata = "\"abc\" \"def\" \"ghi\"";
        GcpCloudDnsProvider.UnquoteTxt(rrdata).ShouldBe("abcdefghi");
    }

    [Fact]
    public void UnquoteTxt_EscapedQuotesInsideChunk_Unescapes()
    {
        var rrdata = "\"hello \\\"world\\\"\"";
        GcpCloudDnsProvider.UnquoteTxt(rrdata).ShouldBe("hello \"world\"");
    }

    [Fact]
    public void UnquoteTxt_UnquotedValue_ReturnedAsIs()
    {
        // Some providers return bare values without quotes
        GcpCloudDnsProvider.UnquoteTxt("plainvalue").ShouldBe("plainvalue");
    }

    // ─── QuoteTxt ─────────────────────────────────────────────────────────────

    [Fact]
    public void QuoteTxt_ShortValue_SingleQuotedChunk()
    {
        var result = GcpCloudDnsProvider.QuoteTxt("v=spf1 include:example.com ~all");
        result.ShouldBe("\"v=spf1 include:example.com ~all\"");
    }

    [Fact]
    public void QuoteTxt_ExactlyMaxLength_SingleQuotedChunk()
    {
        var value = new string('x', 255);
        var result = GcpCloudDnsProvider.QuoteTxt(value);
        result.ShouldBe($"\"{value}\"");
    }

    [Fact]
    public void QuoteTxt_LongValue_SplitsInto255ByteChunks()
    {
        var value = new string('x', 500); // 500 ASCII bytes → two chunks: 255 + 245
        var result = GcpCloudDnsProvider.QuoteTxt(value);

        var parts = result.Split("\" \"");
        parts.Length.ShouldBe(2);
        parts[0].ShouldBe($"\"{new string('x', 255)}");
        parts[1].ShouldBe($"{new string('x', 245)}\"");
    }

    [Fact]
    public void QuoteTxt_InnerQuotes_AreEscaped()
    {
        var result = GcpCloudDnsProvider.QuoteTxt("say \"hello\"");
        result.ShouldBe("\"say \\\"hello\\\"\"");
    }

    // ─── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ShortValue_IsIdentical()
    {
        var original = "v=spf1 include:_spf.google.com ~all";
        GcpCloudDnsProvider.UnquoteTxt(GcpCloudDnsProvider.QuoteTxt(original)).ShouldBe(original);
    }

    [Fact]
    public void RoundTrip_LongDkimValue_IsIdentical()
    {
        // Simulate a real DKIM public key that exceeds 255 bytes
        var original = "v=DKIM1; k=rsa; p=" + new string('M', 400);
        var quoted = GcpCloudDnsProvider.QuoteTxt(original);
        var unquoted = GcpCloudDnsProvider.UnquoteTxt(quoted);
        unquoted.ShouldBe(original);
    }

    [Fact]
    public void RoundTrip_GcpChunkedRrdata_DeleteFormatMatchesStored()
    {
        // Simulate what GCP returns in the rrsets API for a long TXT record:
        // two adjacent quoted chunks where the first is exactly 255 chars.
        var chunk1 = "v=DKIM1; k=rsa; p=" + new string('A', 237); // 255 chars total
        var chunk2 = new string('B', 100) + "QIDAQAB";
        var gcpRrdata = $"\"{chunk1}\" \"{chunk2}\"";

        // Step 1: unquote (as done when reading from GCP)
        var plain = GcpCloudDnsProvider.UnquoteTxt(gcpRrdata);
        plain.ShouldBe(chunk1 + chunk2);

        // Step 2: re-quote for the deletion request
        var deleteRrdata = GcpCloudDnsProvider.QuoteTxt(plain);

        // The deletion rrdata must exactly match what GCP stored
        deleteRrdata.ShouldBe(gcpRrdata);
    }
}
