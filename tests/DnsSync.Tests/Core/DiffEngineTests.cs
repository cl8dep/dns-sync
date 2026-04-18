using DnsSync.Core;
using DnsSync.Core.Records;
using Shouldly;

namespace DnsSync.Tests.Core;

public class DiffEngineTests
{
    private static DnsZone EmptyZone(string name) =>
        new() { Name = name, Records = [] };

    private static DnsZone ZoneWith(string name, params DnsRecord[] records) =>
        new() { Name = name, Records = records };

    [Fact]
    public void Diff_WhenSourceHasRecord_AndTargetIsEmpty_ProducesCreate()
    {
        var source = ZoneWith("example.com.",
            new ARecord { Name = "api.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });
        var target = EmptyZone("example.com.");

        var plan = ZoneDiff.Diff(source, target);

        plan.Creates.ShouldBe(1);
        plan.Updates.ShouldBe(0);
        plan.Deletes.ShouldBe(0);
        plan.Changes[0].ChangeType.ShouldBe(ChangeType.Create);
        plan.Changes[0].After.ShouldBeOfType<ARecord>();
    }

    [Fact]
    public void Diff_WhenTargetHasRecord_AndSourceIsEmpty_ProducesDelete()
    {
        var source = EmptyZone("example.com.");
        var target = ZoneWith("example.com.",
            new ARecord { Name = "old.example.com.", Type = "A", Ttl = 300, Addresses = ["5.6.7.8"] });

        var plan = ZoneDiff.Diff(source, target);

        plan.Deletes.ShouldBe(1);
        plan.Changes[0].ChangeType.ShouldBe(ChangeType.Delete);
    }

    [Fact]
    public void Diff_WhenValuesChange_ProducesUpdate()
    {
        var source = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });
        var target = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["9.9.9.9"] });

        var plan = ZoneDiff.Diff(source, target);

        plan.Updates.ShouldBe(1);
        plan.Changes[0].ChangeType.ShouldBe(ChangeType.Update);
        plan.Changes[0].IsTtlOnlyChange.ShouldBeFalse();
    }

    [Fact]
    public void Diff_WhenOnlyTtlChanges_ProducesUpdateMarkedAsTtlOnly()
    {
        var source = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 600, Addresses = ["1.2.3.4"] });
        var target = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });

        var plan = ZoneDiff.Diff(source, target);

        plan.Updates.ShouldBe(1);
        plan.Changes[0].IsTtlOnlyChange.ShouldBeTrue();
    }

    [Fact]
    public void Diff_WhenRecordsAreIdentical_ProducesNoPlan()
    {
        var source = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });
        var target = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] });

        var plan = ZoneDiff.Diff(source, target);

        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_AlwaysIgnoresSOA()
    {
        var source = ZoneWith("example.com.",
            new ARecord { Name = "example.com.", Type = "SOA", Ttl = 3600, Addresses = ["ns1.example.com."] });
        var target = EmptyZone("example.com.");

        var plan = ZoneDiff.Diff(source, target);

        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_IgnoresApexNsByDefault()
    {
        var source = ZoneWith("example.com.",
            new NsRecord
            {
                Name = "example.com.",
                Type = "NS",
                Ttl = 3600,
                Nameservers = ["ns1.example.com.", "ns2.example.com."]
            });
        var target = EmptyZone("example.com.");

        var plan = ZoneDiff.Diff(source, target, includeApexNs: false);

        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Diff_IncludesApexNsWhenRequested()
    {
        var source = ZoneWith("example.com.",
            new NsRecord
            {
                Name = "example.com.",
                Type = "NS",
                Ttl = 3600,
                Nameservers = ["ns1.example.com.", "ns2.example.com."]
            });
        var target = EmptyZone("example.com.");

        var plan = ZoneDiff.Diff(source, target, includeApexNs: true);

        plan.Creates.ShouldBe(1);
    }

    [Fact]
    public void Diff_SubdomainNsIsNotFiltered()
    {
        var source = ZoneWith("example.com.",
            new NsRecord
            {
                Name = "sub.example.com.",
                Type = "NS",
                Ttl = 3600,
                Nameservers = ["ns1.sub.example.com."]
            });
        var target = EmptyZone("example.com.");

        var plan = ZoneDiff.Diff(source, target);

        plan.Creates.ShouldBe(1);
    }

    [Fact]
    public void Diff_MultipleChanges_ProducesCorrectCounts()
    {
        var source = ZoneWith("example.com.",
            new ARecord { Name = "api.example.com.", Type = "A", Ttl = 300, Addresses = ["1.1.1.1"] },
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["2.2.2.2"] });

        var target = ZoneWith("example.com.",
            new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["9.9.9.9"] },
            new ARecord { Name = "old.example.com.", Type = "A", Ttl = 300, Addresses = ["3.3.3.3"] });

        var plan = ZoneDiff.Diff(source, target);

        plan.Creates.ShouldBe(1);
        plan.Updates.ShouldBe(1);
        plan.Deletes.ShouldBe(1);
    }
}
