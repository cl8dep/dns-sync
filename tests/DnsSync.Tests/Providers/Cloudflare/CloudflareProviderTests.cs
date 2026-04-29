using System.Net;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.Cloudflare;
using DnsSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DnsSync.Tests.Providers.Cloudflare;

/// <summary>
/// Provider-level tests for CloudflareProvider using FakeHttpHandler.
/// Tests verify correct HTTP behavior: URLs, auth headers, request bodies,
/// response parsing, pagination, and error handling.
/// </summary>
public class CloudflareProviderTests
{
    private const string FakeToken = "fake-cf-token";
    private const string ZoneName = "example.com.";
    private const string ZoneId = "abc123";

    private static CloudflareProvider Make(FakeHttpHandler handler) =>
        new(FakeToken, NullLogger<CloudflareProvider>.Instance, handler.CreateClient());

    // ── Auth header ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_SendsBearerTokenHeader()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZonesPage([], totalPages: 1));

        await Make(handler).GetZonesAsync();

        handler.LastRequest.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe(FakeToken);
    }

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_SinglePage_ReturnsAllZones()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZonesPage(["example.com", "other.net"], totalPages: 1));

        var zones = await Make(handler).GetZonesAsync();

        zones.ShouldBe(["example.com.", "other.net."], ignoreOrder: true);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetZonesAsync_MultiPage_FetchesAllPages()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZonesPage(["page1.com"], totalPages: 2));
        handler.Enqueue(ZonesPage(["page2.com"], totalPages: 2));

        var zones = await Make(handler).GetZonesAsync();

        zones.Count.ShouldBe(2);
        zones.ShouldContain("page1.com.");
        zones.ShouldContain("page2.com.");
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].RequestUri!.Query.ShouldContain("page=2");
    }

    [Fact]
    public async Task GetZonesAsync_EmptyResult_ReturnsEmpty()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZonesPage([], totalPages: 1));

        var zones = await Make(handler).GetZonesAsync();

        zones.ShouldBeEmpty();
    }

    // ── GetZoneAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_SendsZoneNameLookupThenRecordsFetch()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZoneIdResponse(ZoneId));
        handler.Enqueue(RecordsPage([], totalPages: 1));

        await Make(handler).GetZoneAsync(ZoneName);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.AbsolutePath.ShouldBe("/client/v4/zones");
        handler.Requests[1].RequestUri!.AbsolutePath.ShouldBe($"/client/v4/zones/{ZoneId}/dns_records");
    }

    [Fact]
    public async Task GetZoneAsync_ParsesARecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZoneIdResponse(ZoneId));
        handler.Enqueue(RecordsPage([ARecordJson("www.example.com", "1.2.3.4")], totalPages: 1));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var a = zone.Records.OfType<ARecord>().ShouldHaveSingleItem();
        a.Name.ShouldBe("www.example.com.");
        a.Addresses.ShouldContain("1.2.3.4");
    }

    [Fact]
    public async Task GetZoneAsync_MergesMultiValueARecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZoneIdResponse(ZoneId));
        handler.Enqueue(RecordsPage([
            ARecordJson("example.com", "1.2.3.4"),
            ARecordJson("example.com", "5.6.7.8")
        ], totalPages: 1));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var a = zone.Records.OfType<ARecord>().ShouldHaveSingleItem();
        a.Addresses.Count.ShouldBe(2);
        a.Addresses.ShouldContain("1.2.3.4");
        a.Addresses.ShouldContain("5.6.7.8");
    }

    [Fact]
    public async Task GetZoneAsync_ParsesTxtRecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZoneIdResponse(ZoneId));
        handler.Enqueue(RecordsPage([TxtRecordJson("example.com", "v=spf1 ~all")], totalPages: 1));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.OfType<TxtRecord>().ShouldHaveSingleItem()
            .Values.ShouldContain("v=spf1 ~all");
    }

    [Fact]
    public async Task GetZoneAsync_MultiPageRecords_FetchesAllPages()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZoneIdResponse(ZoneId));
        handler.Enqueue(RecordsPage([ARecordJson("a.example.com", "1.1.1.1")], totalPages: 2));
        handler.Enqueue(RecordsPage([ARecordJson("b.example.com", "2.2.2.2")], totalPages: 2));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.OfType<ARecord>().Count().ShouldBe(2);
        handler.Requests.Count.ShouldBe(3); // 1 zone lookup + 2 record pages
    }

    [Fact]
    public async Task GetZoneAsync_EmptyZone_ReturnsNoRecords()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZoneIdResponse(ZoneId));
        handler.Enqueue(RecordsPage([], totalPages: 1));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.ShouldBeEmpty();
        zone.Name.ShouldBe(ZoneName);
    }

    // ── PreflightAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PreflightAsync_Success_DoesNotThrow()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"success":true,"result":{"status":"active"}}""");

        await Should.NotThrowAsync(() => Make(handler).PreflightAsync());
    }

    [Fact]
    public async Task PreflightAsync_SuccessFalse_Throws()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"success":false,"errors":[{"message":"invalid token"}]}""");

        await Should.ThrowAsync<InvalidOperationException>(() => Make(handler).PreflightAsync());
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_ServerError_ThrowsHttpRequestException()
    {
        var handler = new FakeHttpHandler();
        // Queue enough 500s to exhaust retries (HttpRetryPolicy retries 3 times)
        for (int i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.InternalServerError, """{"success":false}""");

        await Should.ThrowAsync<HttpRequestException>(() => Make(handler).GetZonesAsync());
    }

    [Fact]
    public async Task GetZoneAsync_ZoneNotFound_ThrowsInvalidOperation()
    {
        var handler = new FakeHttpHandler();
        // Zone lookup returns empty result array
        handler.Enqueue("""{"success":true,"result":[],"result_info":{"total_pages":1}}""");

        await Should.ThrowAsync<InvalidOperationException>(() => Make(handler).GetZoneAsync(ZoneName));
    }

    // ── ApplyPlanAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyPlanAsync_Create_SendsPostRequest()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZoneIdResponse(ZoneId));
        handler.Enqueue(ExistingRecordsMap([]));
        // POST for create
        handler.Enqueue("""{"success":true,"result":{"id":"new-id"}}""");

        var plan = new DnsPlan { Changes = [new RecordChange
        {
            ChangeType = ChangeType.Create,
            After = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }
        }]
        };

        var result = await Make(handler).ApplyPlanAsync(ZoneName, plan);

        result.Applied.ShouldBe(1);
        result.Failed.ShouldBe(0);
        var postRequest = handler.Requests.Last();
        postRequest.Method.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task ApplyPlanAsync_EmptyPlan_MakesNoChangeCalls()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZoneIdResponse(ZoneId));
        handler.Enqueue(ExistingRecordsMap([]));

        var plan = new DnsPlan { Changes = [] };
        await Make(handler).ApplyPlanAsync(ZoneName, plan);

        // Only zone lookup + existing records map calls — no create/patch/delete
        handler.Requests.Count.ShouldBe(2);
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    private static string ZonesPage(IEnumerable<string> names, int totalPages)
    {
        var zoneItems = string.Join(",", names.Select(n => $@"{{""id"":""id-{n}"",""name"":""{n}""}}"));
        return $@"{{""success"":true,""result"":[{zoneItems}],""result_info"":{{""total_pages"":{totalPages},""page"":1}}}}";
    }

    private static string ZoneIdResponse(string id) =>
        $@"{{""success"":true,""result"":[{{""id"":""{id}"",""name"":""example.com""}}],""result_info"":{{""total_pages"":1}}}}";

    private static string RecordsPage(IEnumerable<string> records, int totalPages)
    {
        var items = string.Join(",", records);
        return $@"{{""success"":true,""result"":[{items}],""result_info"":{{""total_pages"":{totalPages},""page"":1}}}}";
    }

    private static string ExistingRecordsMap(IEnumerable<string> records)
    {
        var items = string.Join(",", records);
        return $@"{{""success"":true,""result"":[{items}],""result_info"":{{""total_pages"":1}}}}";
    }

    private static string ARecordJson(string name, string ip, int ttl = 300) =>
        $@"{{""id"":""id-{ip}"",""type"":""A"",""name"":""{name}"",""content"":""{ip}"",""ttl"":{ttl}}}";

    private static string TxtRecordJson(string name, string content, int ttl = 300) =>
        $@"{{""id"":""id-txt"",""type"":""TXT"",""name"":""{name}"",""content"":""{content}"",""ttl"":{ttl}}}";
}
