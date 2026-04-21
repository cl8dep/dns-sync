using DnsSync.Core;
using DnsSync.Core.Records;
using DnsSync.Plan;
using Shouldly;

namespace DnsSync.Tests.Plan;

public class PlanFileSerializerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DnsPlan MakeSimplePlan() => new()
    {
        Changes =
        [
            new RecordChange
            {
                ChangeType = ChangeType.Create,
                After = new ARecord { Name = "www.example.com.", Type = "A", Ttl = 300, Addresses = ["1.2.3.4"] }
            },
            new RecordChange
            {
                ChangeType = ChangeType.Delete,
                Before = new TxtRecord { Name = "example.com.", Type = "TXT", Ttl = 3600, Values = ["v=spf1 ~all"] }
            },
            new RecordChange
            {
                ChangeType = ChangeType.Update,
                Before = new MxRecord { Name = "example.com.", Type = "MX", Ttl = 3600,
                    Values = [new MxValue(10, "mail.example.com.")] },
                After  = new MxRecord { Name = "example.com.", Type = "MX", Ttl = 300,
                    Values = [new MxValue(10, "mail.example.com.")] }
            }
        ]
    };

    private static (string ConfigPath, string PlanPath) WriteTempFiles(string configContent = "providers: {}")
    {
        var configPath = Path.GetTempFileName();
        var planPath   = Path.GetTempFileName();
        File.WriteAllText(configPath, configContent);
        return (configPath, planPath);
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesZoneAndTarget()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            var plan = MakeSimplePlan();
            PlanFileSerializer.Save(configPath, [("example.com.", "cloudflare", plan)], planPath);

            var body = PlanFileSerializer.Load(configPath, planPath);

            body.Zones.ShouldHaveSingleItem();
            body.Zones[0].Zone.ShouldBe("example.com.");
            body.Zones[0].Target.ShouldBe("cloudflare");
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesChangeCount()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            PlanFileSerializer.Save(configPath, [("example.com.", "cf", MakeSimplePlan())], planPath);
            var body = PlanFileSerializer.Load(configPath, planPath);

            body.Zones[0].Changes.Count.ShouldBe(3);
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesChangeTypes()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            PlanFileSerializer.Save(configPath, [("example.com.", "cf", MakeSimplePlan())], planPath);
            var body = PlanFileSerializer.Load(configPath, planPath);

            var changes = body.Zones[0].Changes;
            changes[0].Type.ShouldBe("create");
            changes[1].Type.ShouldBe("delete");
            changes[2].Type.ShouldBe("update");
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesARecordValues()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            PlanFileSerializer.Save(configPath, [("example.com.", "cf", MakeSimplePlan())], planPath);
            var body = PlanFileSerializer.Load(configPath, planPath);

            var create = body.Zones[0].Changes[0];
            create.After!.Ttl.ShouldBe(300);
            create.After.Values.ShouldContain("1.2.3.4");
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesMxEncoding()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            PlanFileSerializer.Save(configPath, [("example.com.", "cf", MakeSimplePlan())], planPath);
            var body = PlanFileSerializer.Load(configPath, planPath);

            var update = body.Zones[0].Changes[2];
            update.Before!.Values.ShouldContain("10 mail.example.com.");
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }

    // ── ToDnsPlan reconstruction ─────────────────────────────────────────────

    [Fact]
    public void ToDnsPlan_ReconstructsARecord()
    {
        var savedZone = new SavedZonePlan
        {
            Zone = "example.com.", Target = "cf",
            Changes =
            [
                new SavedChange
                {
                    Type = "create", Name = "www.example.com.", RecordType = "A",
                    After = new SavedRecord { Ttl = 300, Values = ["1.2.3.4", "5.6.7.8"] }
                }
            ]
        };

        var plan = PlanFileSerializer.ToDnsPlan(savedZone);

        plan.Creates.ShouldBe(1);
        var record = plan.Changes[0].After.ShouldBeOfType<ARecord>();
        record.Addresses.ShouldContain("1.2.3.4");
        record.Addresses.ShouldContain("5.6.7.8");
        record.Ttl.ShouldBe(300);
    }

    [Fact]
    public void ToDnsPlan_ReconstructsMxRecord()
    {
        var savedZone = new SavedZonePlan
        {
            Zone = "example.com.", Target = "cf",
            Changes =
            [
                new SavedChange
                {
                    Type = "create", Name = "example.com.", RecordType = "MX",
                    After = new SavedRecord { Ttl = 3600, Values = ["10 mail.example.com.", "20 mail2.example.com."] }
                }
            ]
        };

        var plan = PlanFileSerializer.ToDnsPlan(savedZone);

        var record = plan.Changes[0].After.ShouldBeOfType<MxRecord>();
        record.Values.Count.ShouldBe(2);
        record.Values[0].Preference.ShouldBe(10);
        record.Values[0].Exchange.ShouldBe("mail.example.com.");
        record.Values[1].Preference.ShouldBe(20);
    }

    [Fact]
    public void ToDnsPlan_ReconstructsSrvRecord()
    {
        var savedZone = new SavedZonePlan
        {
            Zone = "example.com.", Target = "cf",
            Changes =
            [
                new SavedChange
                {
                    Type = "create", Name = "_sip._tcp.example.com.", RecordType = "SRV",
                    After = new SavedRecord { Ttl = 300, Values = ["10 20 443 sip.example.com."] }
                }
            ]
        };

        var plan = PlanFileSerializer.ToDnsPlan(savedZone);

        var record = plan.Changes[0].After.ShouldBeOfType<SrvRecord>();
        record.Values[0].Priority.ShouldBe(10);
        record.Values[0].Weight.ShouldBe(20);
        record.Values[0].Port.ShouldBe(443);
        record.Values[0].Target.ShouldBe("sip.example.com.");
    }

    [Fact]
    public void ToDnsPlan_ReconstructsCaaRecord()
    {
        var savedZone = new SavedZonePlan
        {
            Zone = "example.com.", Target = "cf",
            Changes =
            [
                new SavedChange
                {
                    Type = "create", Name = "example.com.", RecordType = "CAA",
                    After = new SavedRecord { Ttl = 3600, Values = ["0 issue letsencrypt.org"] }
                }
            ]
        };

        var plan = PlanFileSerializer.ToDnsPlan(savedZone);

        var record = plan.Changes[0].After.ShouldBeOfType<CaaRecord>();
        record.Values[0].Flags.ShouldBe(0);
        record.Values[0].Tag.ShouldBe("issue");
        record.Values[0].Value.ShouldBe("letsencrypt.org");
    }

    // ── Signature verification ───────────────────────────────────────────────

    [Fact]
    public void Load_TamperedPlanContent_ThrowsInvalidOperation()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            PlanFileSerializer.Save(configPath, [("example.com.", "cf", MakeSimplePlan())], planPath);

            // Tamper: replace a value inside the saved file
            var content = File.ReadAllText(planPath);
            content = content.Replace("1.2.3.4", "9.9.9.9");
            File.WriteAllText(planPath, content);

            Should.Throw<InvalidOperationException>(() =>
                PlanFileSerializer.Load(configPath, planPath))
                .Message.ShouldContain("tampered");
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }

    [Fact]
    public void Load_ChangedConfig_ThrowsInvalidOperation()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            PlanFileSerializer.Save(configPath, [("example.com.", "cf", MakeSimplePlan())], planPath);

            // Change the config after saving the plan
            File.WriteAllText(configPath, "providers: { changed: true }");

            Should.Throw<InvalidOperationException>(() =>
                PlanFileSerializer.Load(configPath, planPath))
                .Message.ShouldContain("Config file has changed");
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }

    [Fact]
    public void Load_UnmodifiedPlan_DoesNotThrow()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            PlanFileSerializer.Save(configPath, [("example.com.", "cf", MakeSimplePlan())], planPath);
            Should.NotThrow(() => PlanFileSerializer.Load(configPath, planPath));
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }

    [Fact]
    public void Load_MissingPlanFile_ThrowsFileNotFound()
    {
        var configPath = Path.GetTempFileName();
        try
        {
            Should.Throw<FileNotFoundException>(() =>
                PlanFileSerializer.Load(configPath, "/nonexistent/plan.yaml"));
        }
        finally { File.Delete(configPath); }
    }

    // ── YAML format sanity ───────────────────────────────────────────────────

    [Fact]
    public void Save_WritesValidYamlWithSignatureField()
    {
        var (configPath, planPath) = WriteTempFiles();
        try
        {
            PlanFileSerializer.Save(configPath, [("example.com.", "cf", MakeSimplePlan())], planPath);

            var content = File.ReadAllText(planPath);
            content.ShouldContain("signature:");
            content.ShouldContain("sha256:");
            content.ShouldContain("plan:");
        }
        finally { File.Delete(configPath); File.Delete(planPath); }
    }
}
