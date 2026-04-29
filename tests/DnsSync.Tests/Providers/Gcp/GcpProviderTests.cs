using System.Net;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.Gcp;
using DnsSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DnsSync.Tests.Providers.Gcp;

/// <summary>
/// Provider-level tests for GcpCloudDnsProvider using FakeHttpHandler.
/// The internal constructor pre-sets a fake access token so no OAuth calls are made.
/// </summary>
public class GcpProviderTests
{
    private const string Project = "test-project";
    private const string ZoneName = "example.com.";
    private const string ManagedZoneName = "example-com";

    private static GcpCloudDnsProvider Make(FakeHttpHandler handler) =>
        new(Project, NullLogger<GcpCloudDnsProvider>.Instance, handler.CreateClient());

    // ── Auth header ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PreflightAsync_SendsBearerTokenHeader()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZonesListJson([]));

        await Make(handler).PreflightAsync();

        handler.LastRequest.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("fake-token");
    }

    // ── PreflightAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PreflightAsync_Success_DoesNotThrow()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZonesListJson([]));

        await Should.NotThrowAsync(() => Make(handler).PreflightAsync());

        handler.LastRequest.RequestUri!.AbsolutePath.ShouldContain(Project);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldContain("managedZones");
    }

    [Fact]
    public async Task PreflightAsync_ServerError_ThrowsAfterRetries()
    {
        var handler = new FakeHttpHandler();
        for (int i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.InternalServerError, """{"error":{"code":500}}""");

        await Should.ThrowAsync<HttpRequestException>(() => Make(handler).PreflightAsync());
    }

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_ReturnsManagedZoneDnsNames()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZonesListJson(["example.com.", "other.net."]));

        var zones = await Make(handler).GetZonesAsync();

        zones.Count.ShouldBe(2);
        zones.ShouldContain("example.com.");
        zones.ShouldContain("other.net.");
    }

    [Fact]
    public async Task GetZonesAsync_Pagination_FetchesAllPages()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZonesListJson(["page1.com."], nextPageToken: "tok2"));
        handler.Enqueue(ManagedZonesListJson(["page2.net."]));

        var zones = await Make(handler).GetZonesAsync();

        zones.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].RequestUri!.Query.ShouldContain("pageToken=tok2");
    }

    [Fact]
    public async Task GetZonesAsync_EmptyList_ReturnsEmpty()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"managedZones":[]}""");

        var zones = await Make(handler).GetZonesAsync();
        zones.ShouldBeEmpty();
    }

    // ── GetZoneAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_LooksUpManagedZoneThenFetchesRrsets()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue(RrsetsJson([]));

        await Make(handler).GetZoneAsync(ZoneName);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.AbsolutePath.ShouldContain("managedZones");
        handler.Requests[0].RequestUri!.Query.ShouldContain("dnsName");
        handler.Requests[1].RequestUri!.AbsolutePath.ShouldContain(ManagedZoneName);
        handler.Requests[1].RequestUri!.AbsolutePath.ShouldContain("rrsets");
    }

    [Fact]
    public async Task GetZoneAsync_ParsesARecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue(RrsetsJson([
            ARecordJson("www.example.com.", "1.2.3.4", 300)
        ]));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var a = zone.Records.OfType<ARecord>().ShouldHaveSingleItem();
        a.Name.ShouldBe("www.example.com.");
        a.Addresses.ShouldContain("1.2.3.4");
        a.Ttl.ShouldBe(300);
    }

    [Fact]
    public async Task GetZoneAsync_ParsesTxtRecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue(RrsetsJson([
            TxtRecordJson("example.com.", @"""v=spf1 ~all""", 600)
        ]));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.OfType<TxtRecord>().ShouldHaveSingleItem()
            .Values.ShouldContain("v=spf1 ~all");
    }

    [Fact]
    public async Task GetZoneAsync_ParsesMxRecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue(RrsetsJson([
            RrsetJson("example.com.", "MX", 3600, ["10 mail.example.com."])
        ]));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var mx = zone.Records.OfType<MxRecord>().ShouldHaveSingleItem();
        mx.Values.ShouldHaveSingleItem().Exchange.ShouldBe("mail.example.com.");
        mx.Values[0].Preference.ShouldBe(10);
    }

    [Fact]
    public async Task GetZoneAsync_LongTxtValue_ParsesMultipleChunks()
    {
        // GCP stores TXT values > 255 bytes as adjacent quoted chunks:
        //   "first 255 chars" "remaining chars"
        // UnquoteTxt must concatenate them into one plain string.
        var chunk1 = new string('a', 255);
        var chunk2 = new string('b', 100);
        var rrdata = $@"""{chunk1}"" ""{chunk2}""";

        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue(RrsetsJson([
            TxtRecordJson("example.com.", rrdata, 300)
        ]));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var txt = zone.Records.OfType<TxtRecord>().ShouldHaveSingleItem();
        txt.Values[0].ShouldBe(chunk1 + chunk2);
    }

    [Fact]
    public async Task GetZoneAsync_EmptyZone_ReturnsNoRecords()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue(RrsetsJson([]));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.ShouldBeEmpty();
        zone.Name.ShouldBe(ZoneName);
    }

    [Fact]
    public async Task GetZoneAsync_Pagination_FetchesAllRrsetPages()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue(RrsetsJson([ARecordJson("a.example.com.", "1.1.1.1", 300)], nextPageToken: "p2"));
        handler.Enqueue(RrsetsJson([ARecordJson("b.example.com.", "2.2.2.2", 300)]));

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.OfType<ARecord>().Count().ShouldBe(2);
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetZoneAsync_ZoneNotFound_ThrowsInvalidOperation()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"managedZones":[]}""");

        await Should.ThrowAsync<InvalidOperationException>(() => Make(handler).GetZoneAsync(ZoneName));
    }

    // ── ApplyPlanAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyPlanAsync_EmptyPlan_MakesNoCalls()
    {
        var handler = new FakeHttpHandler();
        var result = await Make(handler).ApplyPlanAsync(ZoneName, new DnsPlan { Changes = [] });
        result.Applied.ShouldBe(0);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyPlanAsync_Create_SendsPostToChanges()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue("""{"id":"change-1","status":"pending"}""");

        var plan = new DnsPlan
        {
            Changes = [new RecordChange
        {
            ChangeType = ChangeType.Create,
            After = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }
        }]
        };

        var result = await Make(handler).ApplyPlanAsync(ZoneName, plan);

        result.Applied.ShouldBe(1);
        result.Failed.ShouldBe(0);
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldContain("changes");
    }

    [Fact]
    public async Task ApplyPlanAsync_Update_SendsAtomicDeleteAndAdd()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue("""{"id":"change-2","status":"pending"}""");

        var plan = new DnsPlan
        {
            Changes =
            [
                new RecordChange
                {
                    ChangeType = ChangeType.Update,
                    Before = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.1.1.1"] },
                    After = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["2.2.2.2"] }
                }
            ]
        };

        var result = await Make(handler).ApplyPlanAsync(ZoneName, plan);

        result.Applied.ShouldBe(1);
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        // GCP Update = atomic delete old + add new in the same Changes request
        var body = await handler.LastRequest.Content!.ReadAsStringAsync();
        body.ShouldContain("additions");
        body.ShouldContain("deletions");
    }

    [Fact]
    public async Task ApplyPlanAsync_Delete_SendsDeletionRequest()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        handler.Enqueue("""{"id":"change-3","status":"pending"}""");

        var plan = new DnsPlan
        {
            Changes =
            [
                new RecordChange
                {
                    ChangeType = ChangeType.Delete,
                    Before = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }
                }
            ]
        };

        var result = await Make(handler).ApplyPlanAsync(ZoneName, plan);

        result.Applied.ShouldBe(1);
        var body = await handler.LastRequest.Content!.ReadAsStringAsync();
        body.ShouldContain("deletions");
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_ServerError_ThrowsAfterRetries()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ManagedZoneLookupJson(ManagedZoneName));
        for (int i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.InternalServerError, """{"error":{"code":500}}""");

        await Should.ThrowAsync<HttpRequestException>(() => Make(handler).GetZoneAsync(ZoneName));
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static string ManagedZonesListJson(IEnumerable<string> dnsNames, string? nextPageToken = null)
    {
        var zones = string.Join(",", dnsNames.Select((n, i) =>
            $@"{{""name"":""zone-{i}"",""dnsName"":""{n}"",""visibility"":""public""}}"));
        var token = nextPageToken is not null ? $@",""nextPageToken"":""{nextPageToken}""" : "";
        return $@"{{""managedZones"":[{zones}]{token}}}";
    }

    private static string ManagedZoneLookupJson(string managedZoneName) =>
        $@"{{""managedZones"":[{{""name"":""{managedZoneName}"",""dnsName"":""example.com."",""visibility"":""public""}}]}}";

    private static string RrsetsJson(IEnumerable<string> rrsets, string? nextPageToken = null)
    {
        var items = string.Join(",", rrsets);
        var token = nextPageToken is not null ? $@",""nextPageToken"":""{nextPageToken}""" : "";
        return $@"{{""rrsets"":[{items}]{token}}}";
    }

    private static string ARecordJson(string name, string ip, int ttl) =>
        $@"{{""name"":""{name}"",""type"":""A"",""ttl"":{ttl},""rrdatas"":[""{ip}""]}}";

    private static string TxtRecordJson(string name, string rrdata, int ttl) =>
        // rrdata must be a JSON string value, e.g. "\"v=spf1 ~all\""
        $@"{{""name"":""{name}"",""type"":""TXT"",""ttl"":{ttl},""rrdatas"":[{System.Text.Json.JsonSerializer.Serialize(rrdata)}]}}"; private static string RrsetJson(string name, string type, int ttl, IEnumerable<string> values)
    {
        var vals = string.Join(",", values.Select(v => $@"""{v}"""));
        return $@"{{""name"":""{name}"",""type"":""{type}"",""ttl"":{ttl},""rrdatas"":[{vals}]}}";
    }
}
