using System.Net;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.GoDaddy;
using DnsSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DnsSync.Tests.Providers.GoDaddy;

/// <summary>
/// Provider-level tests for GoDaddyProvider using FakeHttpHandler.
/// </summary>
public class GoDaddyProviderTests
{
    private const string ZoneName = "example.com.";

    private static GoDaddyProvider Make(FakeHttpHandler handler) =>
        new("fake-key", "fake-secret", NullLogger<GoDaddyProvider>.Instance, handler.CreateClient());

    // ── Auth header ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_SendsSsoKeyHeader()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("[]");

        await Make(handler).GetZonesAsync();

        var auth = handler.LastRequest.Headers.Authorization!;
        auth.Scheme.ShouldBe("sso-key");
        auth.Parameter.ShouldBe("fake-key:fake-secret");
    }

    // ── PreflightAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PreflightAsync_Success_DoesNotThrow()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("[]");

        await Should.NotThrowAsync(() => Make(handler).PreflightAsync());
    }

    [Fact]
    public async Task PreflightAsync_Unauthorized_Throws()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"code":"UNABLE_TO_AUTHENTICATE"}""");

        await Should.ThrowAsync<InvalidOperationException>(() => Make(handler).PreflightAsync());
    }

    [Fact]
    public async Task PreflightAsync_Forbidden_Throws()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"code":"UNABLE_TO_AUTHENTICATE"}""");

        await Should.ThrowAsync<InvalidOperationException>(() => Make(handler).PreflightAsync());
    }

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_ReturnsDomains()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""[{"domain":"example.com"},{"domain":"other.net"}]""");

        var zones = await Make(handler).GetZonesAsync();

        zones.Count.ShouldBe(2);
        zones.ShouldContain("example.com.");
        zones.ShouldContain("other.net.");
    }

    [Fact]
    public async Task GetZonesAsync_EmptyList_ReturnsEmpty()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("[]");

        var zones = await Make(handler).GetZonesAsync();
        zones.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetZonesAsync_Pagination_FetchesNextPage()
    {
        var handler = new FakeHttpHandler();
        // First page has exactly 100 items (triggers next page fetch)
        var page1 = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => $$$"""{"domain":"domain{{{i}}}.com"}""")) + "]";
        var page2 = """[{"domain":"last.com"}]""";
        handler.Enqueue(page1);
        handler.Enqueue(page2);

        var zones = await Make(handler).GetZonesAsync();

        zones.Count.ShouldBe(101);
        handler.Requests.Count.ShouldBe(2);
    }

    // ── GetZoneAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_SendsGetToRecordsEndpoint()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("[]");

        await Make(handler).GetZoneAsync(ZoneName);

        handler.LastRequest.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/v1/domains/example.com/records");
    }

    [Fact]
    public async Task GetZoneAsync_ParsesARecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""[{"type":"A","name":"www","data":"1.2.3.4","ttl":600}]""");

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var a = zone.Records.OfType<ARecord>().ShouldHaveSingleItem();
        a.Name.ShouldBe("www.example.com.");
        a.Addresses.ShouldContain("1.2.3.4");
        a.Ttl.ShouldBe(600);
    }

    [Fact]
    public async Task GetZoneAsync_ApexAtSign_ConvertsToFqdn()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""[{"type":"A","name":"@","data":"1.2.3.4","ttl":300}]""");

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var a = zone.Records.OfType<ARecord>().ShouldHaveSingleItem();
        a.Name.ShouldBe("example.com.");
    }

    [Fact]
    public async Task GetZoneAsync_MergesMultiValueRecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""
            [
              {"type":"A","name":"@","data":"1.1.1.1","ttl":300},
              {"type":"A","name":"@","data":"2.2.2.2","ttl":300}
            ]
            """);

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var a = zone.Records.OfType<ARecord>().ShouldHaveSingleItem();
        a.Addresses.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetZoneAsync_ParsesMxRecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""[{"type":"MX","name":"@","data":"mail.example.com","priority":10,"ttl":3600}]""");

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var mx = zone.Records.OfType<MxRecord>().ShouldHaveSingleItem();
        mx.Values.ShouldHaveSingleItem().Exchange.ShouldBe("mail.example.com.");
        mx.Values[0].Preference.ShouldBe(10);
    }

    [Fact]
    public async Task GetZoneAsync_EmptyZone_ReturnsNoRecords()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("[]");

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.ShouldBeEmpty();
        zone.Name.ShouldBe(ZoneName);
    }

    // ── ApplyPlanAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyPlanAsync_EmptyPlan_ReturnsZeroAndMakesNoCalls()
    {
        var handler = new FakeHttpHandler();
        var result = await Make(handler).ApplyPlanAsync(ZoneName, new DnsPlan { Changes = [] });
        result.Applied.ShouldBe(0);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyPlanAsync_Create_SendsPutRequest()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, "");

        var plan = new DnsPlan { Changes = [new RecordChange
        {
            ChangeType = ChangeType.Create,
            After = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }
        }]
        };

        var result = await Make(handler).ApplyPlanAsync(ZoneName, plan);

        result.Applied.ShouldBe(1);
        handler.LastRequest.Method.ShouldBe(HttpMethod.Put);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldContain("/v1/domains/example.com/records/A/www");
    }

    [Fact]
    public async Task ApplyPlanAsync_Delete_SendsDeleteRequest()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.NoContent, "");

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
        handler.LastRequest.Method.ShouldBe(HttpMethod.Delete);
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_ServerError_ThrowsAfterRetries()
    {
        var handler = new FakeHttpHandler();
        for (int i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.InternalServerError, """{"code":"SERVER_ERROR"}""");

        await Should.ThrowAsync<HttpRequestException>(() => Make(handler).GetZoneAsync(ZoneName));
    }
}
