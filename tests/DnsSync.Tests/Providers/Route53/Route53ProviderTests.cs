using System.Net;
using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Providers.Route53;
using DnsSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DnsSync.Tests.Providers.Route53;

/// <summary>
/// Provider-level tests for Route53Provider using FakeHttpHandler.
/// Route53 uses AWS Signature V4 auth and XML request/response format.
/// Tests pass hostedZoneId to skip zone-discovery API calls.
/// </summary>
public class Route53ProviderTests
{
    private const string ZoneName = "example.com.";
    private const string ZoneId = "Z1234567890";
    private static readonly string R53Ns = "https://route53.amazonaws.com/doc/2013-04-01/";

    private static Route53Provider Make(FakeHttpHandler handler, string? hostedZoneId = ZoneId) =>
        new("fake-key", "fake-secret", "us-east-1",
            NullLogger<Route53Provider>.Instance, handler.CreateClient(),
            hostedZoneId: hostedZoneId);

    // ── Auth header ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_SendsAwsAuthorizationHeader()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ListHostedZonesXml([]), contentType: "application/xml");

        await Make(handler, hostedZoneId: null).GetZonesAsync();

        // AWS Sig V4 uses a raw Authorization header starting with "AWS4-HMAC-SHA256"
        var authHeader = handler.LastRequest.Headers.GetValues("Authorization").Single();
        authHeader.ShouldStartWith("AWS4-HMAC-SHA256");
    }

    // ── PreflightAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PreflightAsync_Success_DoesNotThrow()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ListHostedZonesXml([]), contentType: "application/xml");

        await Should.NotThrowAsync(() => Make(handler, hostedZoneId: null).PreflightAsync());

        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/2013-04-01/hostedzone");
    }

    [Fact]
    public async Task PreflightAsync_Unauthorized_ThrowsHttpRequestException()
    {
        var handler = new FakeHttpHandler();
        for (int i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.Forbidden, "<ErrorResponse><Error><Code>InvalidClientTokenId</Code></Error></ErrorResponse>", contentType: "application/xml");

        await Should.ThrowAsync<HttpRequestException>(() => Make(handler, hostedZoneId: null).PreflightAsync());
    }

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_SinglePage_ReturnsAllZones()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ListHostedZonesXml(["example.com.", "other.net."]), contentType: "application/xml");

        var zones = await Make(handler, hostedZoneId: null).GetZonesAsync();

        zones.Count.ShouldBe(2);
        zones.ShouldContain("example.com.");
        zones.ShouldContain("other.net.");
    }

    [Fact]
    public async Task GetZonesAsync_Paginated_FetchesAllPages()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ListHostedZonesXml(["page1.com."], isTruncated: true, nextMarker: "page2"), contentType: "application/xml");
        handler.Enqueue(ListHostedZonesXml(["page2.net."], isTruncated: false), contentType: "application/xml");

        var zones = await Make(handler, hostedZoneId: null).GetZonesAsync();

        zones.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].RequestUri!.Query.ShouldContain("marker=page2");
    }

    [Fact]
    public async Task GetZonesAsync_EmptyList_ReturnsEmpty()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ListHostedZonesXml([]), contentType: "application/xml");

        var zones = await Make(handler, hostedZoneId: null).GetZonesAsync();
        zones.ShouldBeEmpty();
    }

    // ── GetZoneAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_WithConfiguredZoneId_SkipsZoneLookup()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ListResourceRecordSetsXml([]), contentType: "application/xml");

        // hostedZoneId is set — no zone discovery call needed
        await Make(handler, hostedZoneId: ZoneId).GetZoneAsync(ZoneName);

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri!.AbsolutePath.ShouldBe($"/2013-04-01/hostedzone/{ZoneId}/rrset");
    }

    [Fact]
    public async Task GetZoneAsync_ParsesARecord()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ListResourceRecordSetsXml([
            ARecordXml("www.example.com.", "1.2.3.4", 300)
        ]), contentType: "application/xml");

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
        handler.Enqueue(ListResourceRecordSetsXml([
            TxtRecordXml("example.com.", "\"v=spf1 ~all\"", 600)
        ]), contentType: "application/xml");

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.OfType<TxtRecord>().ShouldHaveSingleItem()
            .Values.ShouldContain("v=spf1 ~all");
    }

    [Fact]
    public async Task GetZoneAsync_EmptyZone_ReturnsNoRecords()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ListResourceRecordSetsXml([]), contentType: "application/xml");

        var zone = await Make(handler).GetZoneAsync(ZoneName);

        zone.Records.ShouldBeEmpty();
        zone.Name.ShouldBe(ZoneName);
    }

    [Fact]
    public async Task GetZoneAsync_WithoutConfiguredZoneId_LooksUpZoneFirst()
    {
        var handler = new FakeHttpHandler();
        // Zone lookup
        handler.Enqueue(ZoneByNameXml("example.com.", ZoneId), contentType: "application/xml");
        // Records
        handler.Enqueue(ListResourceRecordSetsXml([]), contentType: "application/xml");

        await Make(handler, hostedZoneId: null).GetZoneAsync(ZoneName);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.Query.ShouldContain("dnsname=example.com");
    }

    // ── ApplyPlanAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyPlanAsync_Create_SendsPostToRrset()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ChangeRrsetResponseXml(), contentType: "application/xml");

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
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldContain("rrset");
    }

    [Fact]
    public async Task ApplyPlanAsync_EmptyPlan_MakesNoCalls()
    {
        var handler = new FakeHttpHandler();
        var result = await Make(handler).ApplyPlanAsync(ZoneName, new DnsPlan { Changes = [] });
        result.Applied.ShouldBe(0);
        handler.Requests.ShouldBeEmpty();
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_ServerError_ThrowsAfterRetries()
    {
        var handler = new FakeHttpHandler();
        for (int i = 0; i < 4; i++)
            handler.Enqueue(HttpStatusCode.InternalServerError, "<ErrorResponse/>", contentType: "application/xml");

        await Should.ThrowAsync<HttpRequestException>(() => Make(handler).GetZoneAsync(ZoneName));
    }

    // ── XML helpers ───────────────────────────────────────────────────────────

    private static string ListHostedZonesXml(
        IEnumerable<string> names,
        bool isTruncated = false,
        string? nextMarker = null)
    {
        var zones = string.Join("", names.Select(n =>
            $"""<HostedZone><Id>/hostedzone/Z{n.GetHashCode():X}</Id><Name>{n}</Name></HostedZone>"""));
        var truncatedEl = isTruncated ? "<IsTruncated>true</IsTruncated>" : "<IsTruncated>false</IsTruncated>";
        var markerEl = nextMarker is not null ? $"<NextMarker>{nextMarker}</NextMarker>" : "";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ListHostedZonesResponse xmlns="{R53Ns}">
              <HostedZones>{zones}</HostedZones>
              {truncatedEl}
              {markerEl}
              <MaxItems>100</MaxItems>
            </ListHostedZonesResponse>
            """;
    }

    private static string ZoneByNameXml(string name, string id) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ListHostedZonesByNameResponse xmlns="{R53Ns}">
          <HostedZones>
            <HostedZone><Id>/hostedzone/{id}</Id><Name>{name}</Name></HostedZone>
          </HostedZones>
          <IsTruncated>false</IsTruncated>
          <MaxItems>1</MaxItems>
        </ListHostedZonesByNameResponse>
        """;

    private static string ListResourceRecordSetsXml(IEnumerable<string> rrsets, bool isTruncated = false) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ListResourceRecordSetsResponse xmlns="{R53Ns}">
          <ResourceRecordSets>{string.Join("", rrsets)}</ResourceRecordSets>
          <IsTruncated>{isTruncated.ToString().ToLower()}</IsTruncated>
          <MaxItems>300</MaxItems>
        </ListResourceRecordSetsResponse>
        """;

    private static string ARecordXml(string name, string ip, int ttl) =>
        $"""
        <ResourceRecordSet>
          <Name>{name}</Name><Type>A</Type><TTL>{ttl}</TTL>
          <ResourceRecords><ResourceRecord><Value>{ip}</Value></ResourceRecord></ResourceRecords>
        </ResourceRecordSet>
        """;

    private static string TxtRecordXml(string name, string content, int ttl) =>
        $"""
        <ResourceRecordSet>
          <Name>{name}</Name><Type>TXT</Type><TTL>{ttl}</TTL>
          <ResourceRecords><ResourceRecord><Value>{content}</Value></ResourceRecord></ResourceRecords>
        </ResourceRecordSet>
        """;

    private static string ChangeRrsetResponseXml() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ChangeResourceRecordSetsResponse xmlns="{R53Ns}">
          <ChangeInfo><Id>/change/C1</Id><Status>PENDING</Status></ChangeInfo>
        </ChangeResourceRecordSetsResponse>
        """;
}
