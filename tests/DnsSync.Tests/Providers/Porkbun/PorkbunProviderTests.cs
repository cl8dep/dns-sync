using System.Net;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.Porkbun;
using DnsSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DnsSync.Tests.Providers.Porkbun;

/// <summary>
/// Provider-level tests for PorkbunProvider using FakeHttpHandler.
/// </summary>
public class PorkbunProviderTests
{
    private const string ZoneName = "example.com.";

    private static PorkbunProvider Make(FakeHttpHandler handler) =>
        new("fake-key", "fake-secret", NullLogger<PorkbunProvider>.Instance, handler.CreateClient());

    // ── PreflightAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PreflightAsync_Success_DoesNotThrow()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"status":"SUCCESS"}""");

        await Should.NotThrowAsync(() => Make(handler).PreflightAsync());
    }

    [Fact]
    public async Task PreflightAsync_Failure_Throws()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"status":"ERROR","message":"Invalid credentials"}""");

        await Should.ThrowAsync<InvalidOperationException>(() => Make(handler).PreflightAsync());
    }

    [Fact]
    public async Task PreflightAsync_SendsPostToPing()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"status":"SUCCESS"}""");

        await Make(handler).PreflightAsync();

        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/json/v3/ping");
    }

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_ReturnsAllDomains()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"status":"SUCCESS","domains":[{"domain":"example.com"},{"domain":"other.net"}]}""");

        var zones = await Make(handler).GetZonesAsync();

        zones.Count.ShouldBe(2);
        zones.ShouldContain("example.com.");
        zones.ShouldContain("other.net.");
    }

    [Fact]
    public async Task GetZonesAsync_EmptyDomains_ReturnsEmpty()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"status":"SUCCESS","domains":[]}""");

        var zones = await Make(handler).GetZonesAsync();
        zones.ShouldBeEmpty();
    }

    // ── GetZoneAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_SendsPostToRetrieve()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"status":"SUCCESS","records":[]}""");

        await Make(handler).GetZoneAsync(ZoneName);

        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/json/v3/dns/retrieve/example.com");
    }

    [Fact]
    public async Task GetZoneAsync_ParsesARecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue($$"""
            {"status":"SUCCESS","records":[
              {"id":"1","name":"www","type":"A","content":"1.2.3.4","ttl":"300"}
            ]}
            """);

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var a = zone.Records.OfType<ARecord>().ShouldHaveSingleItem();
        a.Addresses.ShouldContain("1.2.3.4");
    }

    [Fact]
    public async Task GetZoneAsync_ParsesTxtRecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""
            {"status":"SUCCESS","records":[
              {"id":"2","name":"","type":"TXT","content":"v=spf1 ~all","ttl":"600"}
            ]}
            """);

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.OfType<TxtRecord>().ShouldHaveSingleItem()
            .Values.ShouldContain("v=spf1 ~all");
    }

    [Fact]
    public async Task GetZoneAsync_MergesMultiValueARecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""
            {"status":"SUCCESS","records":[
              {"id":"1","name":"www","type":"A","content":"1.1.1.1","ttl":"300"},
              {"id":"2","name":"www","type":"A","content":"2.2.2.2","ttl":"300"}
            ]}
            """);

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        var a = zone.Records.OfType<ARecord>().ShouldHaveSingleItem();
        a.Addresses.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetZoneAsync_EmptyZone_ReturnsNoRecords()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""{"status":"SUCCESS","records":[]}""");

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.ShouldBeEmpty();
        zone.Name.ShouldBe(ZoneName);
    }

    [Fact]
    public async Task GetZoneAsync_AliasRecord_ConvertedToCname()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue("""
            {"status":"SUCCESS","records":[
              {"id":"3","name":"","type":"ALIAS","content":"example.com.","ttl":"300"}
            ]}
            """);

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        // Porkbun ALIAS records are surfaced as CNAME by the parser
        zone.Records.OfType<CnameRecord>().ShouldHaveSingleItem();
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_ServerError_ThrowsAfterRetries()
    {
        var handler = new FakeHttpHandler();
        for (int i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.InternalServerError, """{"status":"ERROR"}""");

        await Should.ThrowAsync<HttpRequestException>(() => Make(handler).GetZoneAsync(ZoneName));
    }

    // ── ApplyPlanAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyPlanAsync_EmptyPlan_ReturnsZeroApplied()
    {
        var handler = new FakeHttpHandler();
        var result = await Make(handler).ApplyPlanAsync(ZoneName, new DnsPlan { Changes = [] });
        result.Applied.ShouldBe(0);
        result.Failed.ShouldBe(0);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyPlanAsync_Create_SendsPostRequest()
    {
        var handler = new FakeHttpHandler();
        // GetExistingRecordIds call
        handler.Enqueue("""{"status":"SUCCESS","records":[]}""");
        // Create call
        handler.Enqueue("""{"status":"SUCCESS","id":"new-id"}""");

        var plan = new DnsPlan { Changes = [new RecordChange
        {
            ChangeType = ChangeType.Create,
            After = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }
        }]
        };

        var result = await Make(handler).ApplyPlanAsync(ZoneName, plan);

        result.Applied.ShouldBe(1);
        result.Failed.ShouldBe(0);
    }

    [Fact]
    public async Task ApplyPlanAsync_Delete_CallsDeleteEndpoint()
    {
        var handler = new FakeHttpHandler();
        // GetExistingRecordIds
        handler.Enqueue("""
            {"status":"SUCCESS","records":[
              {"id":"del-id","name":"www","type":"A","content":"1.2.3.4","ttl":"300"}
            ]}
            """);
        // Delete call
        handler.Enqueue("""{"status":"SUCCESS"}""");

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
        var deleteReq = handler.Requests.Last();
        deleteReq.Method.ShouldBe(HttpMethod.Post);
        deleteReq.RequestUri!.AbsolutePath.ShouldContain("delete");
        deleteReq.RequestUri!.AbsolutePath.ShouldContain("del-id");
    }
}
