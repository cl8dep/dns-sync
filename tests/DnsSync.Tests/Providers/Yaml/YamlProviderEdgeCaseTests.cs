using DnsSync.Core.Records;
using DnsSync.Providers.Yaml;
using Shouldly;

namespace DnsSync.Tests.Providers.Yaml;

/// <summary>
/// Edge-case tests for YamlProvider: filesystem edge cases (spaces in path, hyphens,
/// non-YAML files), malformed YAML, TTL defaults, TXT escape handling, and
/// GetZonesAsync zone name normalization.
/// </summary>
public class YamlProviderEdgeCaseTests : IDisposable
{
    private readonly string _dir;

    public YamlProviderEdgeCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dns sync edge-cases " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string filename, string content)
    {
        var path = Path.Combine(_dir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    // ── filesystem edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task GetZoneAsync_DirectoryPathWithSpaces_ReadsCorrectly()
    {
        // _dir already contains spaces in the path ("dns sync edge-cases …")
        Write("example.com.yaml",
            "'': \n  type: A\n  ttl: 300\n  value: 1.2.3.4\n");

        var provider = new YamlProvider(_dir);
        var zone = await provider.GetZoneAsync("example.com.");

        zone.Records.OfType<ARecord>().Single().Addresses.ShouldContain("1.2.3.4");
    }

    [Fact]
    public async Task GetZoneAsync_ZoneNameWithHyphens_ReadsCorrectly()
    {
        Write("my-zone.com.yaml",
            "'': \n  type: A\n  ttl: 300\n  value: 5.6.7.8\n");

        var provider = new YamlProvider(_dir);
        var zone = await provider.GetZoneAsync("my-zone.com.");

        zone.Name.ShouldBe("my-zone.com.");
        zone.Records.OfType<ARecord>().Single().Addresses.ShouldContain("5.6.7.8");
    }

    [Fact]
    public async Task GetZoneAsync_ZoneNameWithSubdomain_ReadsCorrectly()
    {
        Write("sub.example.com.yaml",
            "www: \n  type: A\n  ttl: 300\n  value: 9.9.9.9\n");

        var provider = new YamlProvider(_dir);
        var zone = await provider.GetZoneAsync("sub.example.com.");

        zone.Name.ShouldBe("sub.example.com.");
        zone.Records.OfType<ARecord>().Single().Name.ShouldBe("www.sub.example.com.");
    }

    [Fact]
    public async Task GetZoneAsync_MissingFile_ThrowsFileNotFoundException()
    {
        var provider = new YamlProvider(_dir);
        await Should.ThrowAsync<FileNotFoundException>(
            () => provider.GetZoneAsync("doesnotexist.com."));
    }

    [Fact]
    public async Task PreflightAsync_DirectoryMissing_ThrowsDirectoryNotFoundException()
    {
        var provider = new YamlProvider("/nonexistent-dir-xyz-123");
        await Should.ThrowAsync<DirectoryNotFoundException>(
            () => provider.PreflightAsync());
    }

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_EmptyDirectory_ReturnsEmpty()
    {
        var provider = new YamlProvider(_dir);
        var zones = await provider.GetZonesAsync();
        zones.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetZonesAsync_DirectoryWithNonYamlFiles_OnlyReturnsYamlZones()
    {
        Write("example.com.yaml", "'': \n  type: A\n  ttl: 300\n  value: 1.2.3.4\n");
        Write("readme.txt", "not a zone");
        Write("example.json", "{}");
        Write("backup.bak", "old data");
        Write("notes.md", "# notes");

        var provider = new YamlProvider(_dir);
        var zones = await provider.GetZonesAsync();

        zones.Count.ShouldBe(1);
        zones.ShouldContain("example.com.");
    }

    [Fact]
    public async Task GetZonesAsync_ZoneFileNames_NormalizedToFqdn()
    {
        // Zone files without trailing dot → should be normalized to FQDN with trailing dot
        Write("example.com.yaml", "'': \n  type: A\n  ttl: 300\n  value: 1.2.3.4\n");

        var provider = new YamlProvider(_dir);
        var zones = await provider.GetZonesAsync();

        zones.Single().ShouldEndWith(".");
    }

    [Fact]
    public async Task GetZonesAsync_MultipleZoneFiles_ReturnsAll()
    {
        Write("alpha.com.yaml", "'': \n  type: A\n  ttl: 300\n  value: 1.1.1.1\n");
        Write("beta.net.yaml", "'': \n  type: A\n  ttl: 300\n  value: 2.2.2.2\n");
        Write("gamma.io.yaml", "'': \n  type: A\n  ttl: 300\n  value: 3.3.3.3\n");

        var provider = new YamlProvider(_dir);
        var zones = await provider.GetZonesAsync();

        zones.Count.ShouldBe(3);
    }

    // ── malformed / incomplete YAML ───────────────────────────────────────────

    [Fact]
    public void ParseZoneYaml_EmptyFile_ReturnsNoRecords()
    {
        var records = YamlProvider.ParseZoneYaml("", "example.com.");
        records.ShouldBeEmpty();
    }

    [Fact]
    public void ParseZoneYaml_OnlyComments_ReturnsNoRecords()
    {
        var yaml = "# This file has only comments\n# No records here\n";
        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        records.ShouldBeEmpty();
    }

    [Fact]
    public void ParseZoneYaml_UnknownRecordType_IsSkipped()
    {
        var yaml = """
            www:
              type: UNKNOWN
              ttl: 300
              value: 1.2.3.4
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        records.ShouldBeEmpty();
    }

    [Fact]
    public void ParseZoneYaml_RecordWithNoType_IsSkipped()
    {
        var yaml = """
            www:
              ttl: 300
              value: 1.2.3.4
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        records.ShouldBeEmpty();
    }

    [Fact]
    public void ParseZoneYaml_RecordWithNoTtl_DefaultsTo3600()
    {
        var yaml = """
            www:
              type: A
              value: 1.2.3.4
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        records.OfType<ARecord>().Single().Ttl.ShouldBe(3600);
    }

    [Fact]
    public void ParseZoneYaml_TxtWithSemicolonEscape_UnescapesCorrectly()
    {
        // octodns / Porkbun emit \; — should be normalized to ;
        var yaml = """
            '':
              type: TXT
              ttl: 300
              value: v=spf1 include:example.com\; -all
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        var txt = records.OfType<TxtRecord>().Single();
        txt.Values.Single().ShouldBe("v=spf1 include:example.com; -all");
        txt.Values.Single().ShouldNotContain("\\;");
    }

    // ── YAML key edge cases ───────────────────────────────────────────────────

    [Fact]
    public void ParseZoneYaml_SubdomainKeyAtSign_IsApex()
    {
        var yaml = """
            '@':
              type: A
              ttl: 300
              value: 1.2.3.4
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        records.OfType<ARecord>().Single().Name.ShouldBe("example.com.");
    }

    [Fact]
    public void ParseZoneYaml_SubdomainKeyEmpty_IsApex()
    {
        var yaml = """
            '':
              type: A
              ttl: 300
              value: 1.2.3.4
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        records.OfType<ARecord>().Single().Name.ShouldBe("example.com.");
    }

    [Fact]
    public void ParseZoneYaml_AbsoluteFqdnKey_PreservedAsIs()
    {
        // Key ending with '.' is treated as absolute FQDN, not relative
        var yaml = """
            other.example.com.:
              type: A
              ttl: 300
              value: 2.2.2.2
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        records.OfType<ARecord>().Single().Name.ShouldBe("other.example.com.");
    }

    [Fact]
    public void ParseZoneYaml_ListFormWithUnknownType_SkipsUnknownKeepsValid()
    {
        var yaml = """
            '':
              -
                type: A
                ttl: 300
                value: 1.2.3.4
              -
                type: BOGUS
                ttl: 300
                value: nonsense
            """;

        var records = YamlProvider.ParseZoneYaml(yaml, "example.com.");
        // Only the A record should survive
        records.Count.ShouldBe(1);
        records.OfType<ARecord>().ShouldHaveSingleItem();
    }
}
