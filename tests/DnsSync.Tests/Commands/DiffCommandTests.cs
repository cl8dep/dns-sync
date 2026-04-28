using DnsSync.Core;
using DnsSync.Core.Records;
using Shouldly;

namespace DnsSync.Tests.Commands;

/// <summary>
/// Tests for the diff command logic: zone discovery, empty-target handling, and plan production.
/// These tests exercise ZoneDiff directly with stub zones, matching what DiffCommand does internally.
/// </summary>
public class DiffCommandTests
{
    private static DnsZone ZoneWith(string name, params DnsRecord[] records) =>
        new() { Name = name, Records = records };

    private static DnsZone EmptyZone(string name) =>
        new() { Name = name, Records = [] };

    // ─── Both zones identical ─────────────────────────────────────────────────

    [Fact]
    public void Diff_IdenticalZones_ProducesNoPlan()
    {
        var from = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });
        var to = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });

        var plan = ZoneDiff.Diff(from, to);

        plan.IsEmpty.ShouldBeTrue();
        plan.Total.ShouldBe(0);
    }

    // ─── Record in from but not in to ─────────────────────────────────────────

    [Fact]
    public void Diff_RecordInFromNotInTo_ProducesCreate()
    {
        var from = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });
        var to = EmptyZone("example.com.");

        var plan = ZoneDiff.Diff(from, to);

        plan.Creates.ShouldBe(1);
        plan.Updates.ShouldBe(0);
        plan.Deletes.ShouldBe(0);
        plan.Changes[0].ChangeType.ShouldBe(ChangeType.Create);
        plan.Changes[0].After.ShouldBeOfType<ARecord>();
    }

    // ─── Record in to but not in from ─────────────────────────────────────────

    [Fact]
    public void Diff_RecordInToNotInFrom_ProducesDelete()
    {
        var from = EmptyZone("example.com.");
        var to = ZoneWith("example.com.",
            new ARecord { Name = "old.example.com.", Type = "A", Ttl = 300, Addresses = ["9.9.9.9"] });

        var plan = ZoneDiff.Diff(from, to);

        plan.Deletes.ShouldBe(1);
        plan.Changes[0].ChangeType.ShouldBe(ChangeType.Delete);
    }

    // ─── Records differ between providers ────────────────────────────────────

    [Fact]
    public void Diff_RecordValuesDiffer_ProducesUpdate()
    {
        var from = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });
        var to = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["9.9.9.9"] });

        var plan = ZoneDiff.Diff(from, to);

        plan.Updates.ShouldBe(1);
        plan.Changes[0].ChangeType.ShouldBe(ChangeType.Update);
        plan.Changes[0].IsTtlOnlyChange.ShouldBeFalse();
    }

    // ─── Target zone not found → treated as empty ────────────────────────────

    [Fact]
    public void Diff_TargetZoneNotFound_AllRecordsAreCreates()
    {
        var from = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] },
            new MxRecord { Name = "example.com.", Type = "MX", Ttl = 600, Values = [new MxValue(10, "mail.example.com.")] });

        // Simulate target zone not found: substitute empty zone (DiffCommand behavior)
        var to = EmptyZone("example.com.");

        var plan = ZoneDiff.Diff(from, to);

        plan.Creates.ShouldBe(2);
        plan.Deletes.ShouldBe(0);
    }

    // ─── Multiple record types ────────────────────────────────────────────────

    [Fact]
    public void Diff_MultipleRecordTypes_EachDiffedIndependently()
    {
        var from = ZoneWith("example.com.",
            new ARecord { Name = "example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] },
            new TxtRecord { Name = "example.com.", Type = "TXT", Ttl = 300, Values = ["v=spf1 ~all"] },
            new MxRecord { Name = "example.com.", Type = "MX", Ttl = 600, Values = [new MxValue(10, "mail.example.com.")] });

        var to = ZoneWith("example.com.",
            new ARecord { Name = "example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] },  // same
            new TxtRecord { Name = "example.com.", Type = "TXT", Ttl = 300, Values = ["v=spf1 include:sendgrid.net ~all"] }); // changed — MX missing

        var plan = ZoneDiff.Diff(from, to);

        plan.Creates.ShouldBe(1);  // MX
        plan.Updates.ShouldBe(1);  // TXT
        plan.Deletes.ShouldBe(0);
    }

    // ─── --include-apex-ns flag ───────────────────────────────────────────────

    [Fact]
    public void Diff_ApexNsExcludedByDefault()
    {
        var from = ZoneWith("example.com.",
            new NsRecord { Name = "example.com.", Type = "NS", Ttl = 3600, Nameservers = ["ns1.from.com.", "ns2.from.com."] });
        var to = ZoneWith("example.com.",
            new NsRecord { Name = "example.com.", Type = "NS", Ttl = 3600, Nameservers = ["ns1.to.com.", "ns2.to.com."] });

        var planExcluded = ZoneDiff.Diff(from, to, includeApexNs: false);
        var planIncluded = ZoneDiff.Diff(from, to, includeApexNs: true);

        planExcluded.IsEmpty.ShouldBeTrue();
        planIncluded.Updates.ShouldBe(1);
    }

    // ─── TXT record normalization ─────────────────────────────────────────────

    [Fact]
    public void Diff_TxtRecordsIdentical_NoDiff()
    {
        var from = ZoneWith("example.com.",
            new TxtRecord { Name = "_dmarc.example.com.", Type = "TXT", Ttl = 300, Values = ["v=DMARC1; p=reject"] });
        var to = ZoneWith("example.com.",
            new TxtRecord { Name = "_dmarc.example.com.", Type = "TXT", Ttl = 300, Values = ["v=DMARC1; p=reject"] });

        var plan = ZoneDiff.Diff(from, to);

        plan.IsEmpty.ShouldBeTrue();
    }
}
