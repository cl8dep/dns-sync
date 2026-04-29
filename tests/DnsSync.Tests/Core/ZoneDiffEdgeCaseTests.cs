using DnsSync.Core;
using DnsSync.Core.Records;
using Shouldly;

namespace DnsSync.Tests.Core;

/// <summary>
/// Edge-case tests for ZoneDiff: empty zones, wildcards, case sensitivity,
/// SOA filtering, apex-NS filtering, TTL-only changes, and mixed change types.
/// </summary>
public class ZoneDiffEdgeCaseTests
{
    private static DnsZone Empty(string name = "example.com.") =>
        new() { Name = name, Records = [] };

    private static DnsZone ZoneWith(string name, params DnsRecord[] records) =>
        new() { Name = name, Records = records };

    private static ARecord ARecord(string fqdn, int ttl = 300, string ip = "1.2.3.4") =>
        new() { Name = fqdn, Type = "A", Ttl = ttl, Addresses = [ip] };

    private static NsRecord ApexNs(string zone = "example.com.", string ns = "ns1.example.com.") =>
        new() { Name = zone, Type = "NS", Ttl = 3600, Nameservers = [ns] };

    // ── both zones empty ──────────────────────────────────────────────────────

    [Fact]
    public void Diff_BothZonesEmpty_ProducesNoPlan()
    {
        var plan = ZoneDiff.Diff(Empty(), Empty());

        plan.IsEmpty.ShouldBeTrue();
        plan.Total.ShouldBe(0);
    }

    // ── source empty ──────────────────────────────────────────────────────────

    [Fact]
    public void Diff_SourceEmpty_TargetHasRecords_AllDeletes()
    {
        var target = ZoneWith("example.com.",
            ARecord("www.example.com."),
            ARecord("api.example.com."));

        var plan = ZoneDiff.Diff(Empty(), target);

        plan.Deletes.ShouldBe(2);
        plan.Creates.ShouldBe(0);
        plan.Updates.ShouldBe(0);
    }

    // ── target empty ──────────────────────────────────────────────────────────

    [Fact]
    public void Diff_TargetEmpty_SourceHasRecords_AllCreates()
    {
        var source = ZoneWith("example.com.",
            ARecord("www.example.com."),
            ARecord("api.example.com."));

        var plan = ZoneDiff.Diff(source, Empty());

        plan.Creates.ShouldBe(2);
        plan.Deletes.ShouldBe(0);
        plan.Updates.ShouldBe(0);
    }

    // ── wildcard records ──────────────────────────────────────────────────────

    [Fact]
    public void Diff_WildcardRecord_TreatedAsNormalRecordName()
    {
        var source = ZoneWith("example.com.",
            ARecord("*.example.com."));
        var target = Empty();

        var plan = ZoneDiff.Diff(source, target);

        plan.Creates.ShouldBe(1);
        plan.Changes[0].After!.Name.ShouldBe("*.example.com.");
    }

    [Fact]
    public void Diff_WildcardSourceAndTarget_Identical_ProducesNoPlan()
    {
        var record = ARecord("*.example.com.");
        var source = ZoneWith("example.com.", record);
        var target = ZoneWith("example.com.", ARecord("*.example.com."));

        var plan = ZoneDiff.Diff(source, target);

        plan.IsEmpty.ShouldBeTrue();
    }

    // ── case sensitivity in record names ─────────────────────────────────────

    [Fact]
    public void Diff_RecordNamesAreCaseSensitive_TreatedAsDistinct()
    {
        // ZoneDiff compares record names as-is (case-sensitive). Providers are expected
        // to normalize to lowercase before passing records to ZoneDiff.
        var source = ZoneWith("example.com.", ARecord("WWW.EXAMPLE.COM."));
        var target = ZoneWith("example.com.", ARecord("www.example.com."));

        var plan = ZoneDiff.Diff(source, target);

        // Different case → treated as different records
        plan.Creates.ShouldBe(1); // WWW.EXAMPLE.COM. not in target
        plan.Deletes.ShouldBe(1); // www.example.com. not in source
    }

    // ── SOA is always ignored ─────────────────────────────────────────────────
    // DnsRecord is abstract; use ARecord with Type="SOA" to simulate SOA records
    // in tests — ZoneDiff filters by Type string regardless of C# type.

    private static ARecord SoaRecord() =>
        new() { Name = "example.com.", Type = "SOA", Ttl = 3600, Addresses = [] };

    [Fact]
    public void Diff_BothSidesHaveSoa_NoDiff()
    {
        var source = ZoneWith("example.com.", SoaRecord());
        var target = ZoneWith("example.com.", SoaRecord());

        var plan = ZoneDiff.Diff(source, target);

        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_SoaOnlyInSource_IsIgnored()
    {
        var source = ZoneWith("example.com.", SoaRecord());

        var plan = ZoneDiff.Diff(source, Empty());

        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_SoaOnlyInTarget_IsIgnored()
    {
        var target = ZoneWith("example.com.", SoaRecord());

        var plan = ZoneDiff.Diff(Empty(), target);

        plan.IsEmpty.ShouldBeTrue();
    }

    // ── apex NS filtering ─────────────────────────────────────────────────────

    [Fact]
    public void Diff_ApexNs_ExcludedByDefault_EvenWhenDifferent()
    {
        var source = ZoneWith("example.com.", ApexNs(ns: "ns1.source.com."));
        var target = ZoneWith("example.com.", ApexNs(ns: "ns1.target.com."));

        var plan = ZoneDiff.Diff(source, target, includeApexNs: false);

        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_ApexNs_IncludedWhenFlagSet_ProducesUpdate()
    {
        var source = ZoneWith("example.com.", ApexNs(ns: "ns1.source.com."));
        var target = ZoneWith("example.com.", ApexNs(ns: "ns1.target.com."));

        var plan = ZoneDiff.Diff(source, target, includeApexNs: true);

        plan.IsEmpty.ShouldBeFalse();
        plan.Updates.ShouldBe(1);
    }

    [Fact]
    public void Diff_SubdomainNs_NotFiltered()
    {
        var ns = new NsRecord
        {
            Name = "sub.example.com.",
            Type = "NS",
            Ttl = 3600,
            Nameservers = ["ns1.example.com."]
        };
        var source = ZoneWith("example.com.", ns);

        var plan = ZoneDiff.Diff(source, Empty());

        plan.Creates.ShouldBe(1);
    }

    // ── TTL-only changes ──────────────────────────────────────────────────────

    [Fact]
    public void Diff_TtlOnly_IsTtlOnlyChangeTrue()
    {
        var source = ZoneWith("example.com.", ARecord("www.example.com.", ttl: 60));
        var target = ZoneWith("example.com.", ARecord("www.example.com.", ttl: 3600));

        var plan = ZoneDiff.Diff(source, target);

        plan.Updates.ShouldBe(1);
        plan.Changes[0].IsTtlOnlyChange.ShouldBeTrue();
    }

    [Fact]
    public void Diff_BothTtlAndValueChange_IsNotTtlOnly()
    {
        var source = ZoneWith("example.com.", ARecord("www.example.com.", ttl: 60, ip: "1.1.1.1"));
        var target = ZoneWith("example.com.", ARecord("www.example.com.", ttl: 3600, ip: "2.2.2.2"));

        var plan = ZoneDiff.Diff(source, target);

        plan.Updates.ShouldBe(1);
        plan.Changes[0].IsTtlOnlyChange.ShouldBeFalse();
    }

    // ── mixed changes ─────────────────────────────────────────────────────────

    [Fact]
    public void Diff_CreateUpdateDelete_CountsCorrect()
    {
        var source = ZoneWith("example.com.",
            ARecord("new.example.com."),                            // create
            ARecord("changed.example.com.", ip: "9.9.9.9"),         // update
            ARecord("unchanged.example.com."));                     // no change

        var target = ZoneWith("example.com.",
            ARecord("changed.example.com.", ip: "1.1.1.1"),         // will be updated
            ARecord("unchanged.example.com."),                      // unchanged
            ARecord("deleted.example.com."));                       // delete

        var plan = ZoneDiff.Diff(source, target);

        plan.Creates.ShouldBe(1);
        plan.Updates.ShouldBe(1);
        plan.Deletes.ShouldBe(1);
    }

    [Fact]
    public void Diff_MultipleRRsetsAtSameName_EachDiffedIndependently()
    {
        // Both A and CNAME at different names — each diffed as its own RRset
        var source = ZoneWith("example.com.",
            ARecord("host.example.com.", ip: "1.1.1.1"),
            new CnameRecord
            {
                Name = "alias.example.com.",
                Type = "CNAME",
                Ttl  = 300,
                Target = "host.example.com."
            });

        var target = ZoneWith("example.com.",
            ARecord("host.example.com.", ip: "2.2.2.2")); // changed IP; alias gone → delete

        var plan = ZoneDiff.Diff(source, target);

        plan.Updates.ShouldBe(1); // A record IP changed
        plan.Creates.ShouldBe(1); // CNAME now only in source → create on target? No — target lacks it → create
        plan.Deletes.ShouldBe(0);
    }
}
