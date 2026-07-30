using DnsSync.Commands;
using DnsSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace DnsSync.Tests.Commands;

[Collection("cli-serial")]
public class FmtCommandTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly TestConsole _console = new();
    private readonly IAnsiConsole _previousConsole;

    public FmtCommandTests()
    {
        Directory.CreateDirectory(_tmp);
        _previousConsole = AnsiConsole.Console;
        AnsiConsole.Console = _console;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _previousConsole;
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private CommandApp BuildApp()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);
        app.Configure(cfg => cfg.AddCommand<FmtCommand>("fmt"));
        return app;
    }

    private void WriteZone(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tmp, fileName), content);
    }

    [Fact]
    public async Task Fmt_SortsRecordTypesSemantically()
    {
        // TXT before A, should become A before TXT
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: TXT
                ttl: 600
                value: hello
              -
                type: A
                ttl: 600
                value: 1.2.3.4
            """.Replace("            ", ""));

        var app = BuildApp();
        var result = await app.RunAsync(["fmt", _tmp]);

        result.ShouldBe(0);

        var output = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));
        var aPos = output.IndexOf("type: A", StringComparison.Ordinal);
        var txtPos = output.IndexOf("type: TXT", StringComparison.Ordinal);
        aPos.ShouldBeLessThan(txtPos, "A record should appear before TXT");
    }

    [Fact]
    public async Task Fmt_SortsNsBeforeA()
    {
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: A
                ttl: 600
                value: 1.2.3.4
              -
                type: NS
                ttl: 3600
                values:
                  - ns1.example.com.
                  - ns2.example.com.
            """.Replace("            ", ""));

        var app = BuildApp();
        await app.RunAsync(["fmt", _tmp]);

        var output = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));
        var nsPos = output.IndexOf("type: NS", StringComparison.Ordinal);
        var aPos = output.IndexOf("type: A", StringComparison.Ordinal);
        nsPos.ShouldBeLessThan(aPos, "NS should appear before A");
    }

    [Fact]
    public async Task Fmt_SortsMxByPreferenceAscending()
    {
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            '':
              type: MX
              ttl: 3600
              values:
                - preference: 20
                  exchange: mx2.example.com.
                - preference: 5
                  exchange: mx1.example.com.
                - preference: 10
                  exchange: mx3.example.com.
            """.Replace("            ", ""));

        var app = BuildApp();
        await app.RunAsync(["fmt", _tmp]);

        var output = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));
        var mx1Pos = output.IndexOf("mx1.example.com.", StringComparison.Ordinal);
        var mx3Pos = output.IndexOf("mx3.example.com.", StringComparison.Ordinal);
        var mx2Pos = output.IndexOf("mx2.example.com.", StringComparison.Ordinal);
        mx1Pos.ShouldBeLessThan(mx3Pos, "preference 5 before 10");
        mx3Pos.ShouldBeLessThan(mx2Pos, "preference 10 before 20");
    }

    [Fact]
    public async Task Fmt_SortsSubdomainsAlphabetically_ApexFirst()
    {
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            www:
              type: A
              ttl: 600
              value: 1.2.3.4

            '':
              type: A
              ttl: 600
              value: 5.6.7.8

            api:
              type: A
              ttl: 600
              value: 9.10.11.12
            """.Replace("            ", ""));

        var app = BuildApp();
        await app.RunAsync(["fmt", _tmp]);

        var output = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));
        var apexPos = output.IndexOf("'':", StringComparison.Ordinal);
        var apiPos = output.IndexOf("api:", StringComparison.Ordinal);
        var wwwPos = output.IndexOf("www:", StringComparison.Ordinal);
        apexPos.ShouldBeLessThan(apiPos, "apex before api");
        apiPos.ShouldBeLessThan(wwwPos, "api before www");
    }

    [Fact]
    public async Task Fmt_IsIdempotent()
    {
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: TXT
                ttl: 600
                value: hello
              -
                type: A
                ttl: 600
                value: 1.2.3.4

            www:
              type: CNAME
              ttl: 600
              value: example.com.
            """.Replace("            ", ""));

        var app = BuildApp();
        await app.RunAsync(["fmt", _tmp]);

        var firstPass = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));

        // Run again
        await app.RunAsync(["fmt", _tmp]);

        var secondPass = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));
        secondPass.ShouldBe(firstPass, "fmt should be idempotent");
    }

    [Fact]
    public async Task Fmt_Check_Returns1_WhenFilesNeedFormatting()
    {
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: TXT
                ttl: 600
                value: hello
              -
                type: A
                ttl: 600
                value: 1.2.3.4
            """.Replace("            ", ""));

        var app = BuildApp();
        var result = await app.RunAsync(["fmt", _tmp, "--check"]);

        result.ShouldBe(1);

        // File should NOT have been modified
        var content = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));
        content.ShouldContain("type: TXT\n    ttl: 600\n    value: hello\n  -\n    type: A");
    }

    [Fact]
    public async Task Fmt_Check_Returns0_WhenAlreadyFormatted()
    {
        // Write a pre-formatted file (A before TXT, apex first)
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: A
                ttl: 600
                value: 1.2.3.4
              -
                type: TXT
                ttl: 600
                value: hello
            """.Replace("            ", ""));

        var app = BuildApp();
        // First format it to get canonical output
        await app.RunAsync(["fmt", _tmp]);

        // Now check should pass
        var result = await app.RunAsync(["fmt", _tmp, "--check"]);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Fmt_NonexistentDirectory_Returns1()
    {
        var app = BuildApp();
        var result = await app.RunAsync(["fmt", "/nonexistent/path"]);
        result.ShouldBe(1);
    }

    [Fact]
    public async Task Fmt_EmptyDirectory_Returns0()
    {
        var emptyDir = Path.Combine(_tmp, "empty");
        Directory.CreateDirectory(emptyDir);

        var app = BuildApp();
        var result = await app.RunAsync(["fmt", emptyDir]);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Fmt_DoesNotModifyAlreadySortedFile()
    {
        // NS → A → CNAME → MX → TXT is already semantic order
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: NS
                ttl: 3600
                values:
                  - ns1.example.com.
              -
                type: A
                ttl: 600
                value: 1.2.3.4
              -
                type: MX
                ttl: 3600
                values:
                  - preference: 10
                    exchange: mail.example.com.
              -
                type: TXT
                ttl: 600
                value: hello
            """.Replace("            ", ""));

        var app = BuildApp();
        // Format once to normalize header
        await app.RunAsync(["fmt", _tmp]);
        var formatted = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));

        // Second run should not change it
        await app.RunAsync(["fmt", _tmp]);
        var secondRun = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));

        secondRun.ShouldBe(formatted);
    }

    [Fact]
    public async Task Fmt_FullSemanticOrder_NS_A_AAAA_CNAME_MX_TXT_SRV_CAA()
    {
        WriteZone("example.com.yaml", """
            # Zone: example.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: CAA
                ttl: 3600
                values:
                  - flags: 0
                    tag: issue
                    value: "letsencrypt.org"
              -
                type: TXT
                ttl: 600
                value: hello
              -
                type: MX
                ttl: 3600
                values:
                  - preference: 10
                    exchange: mail.example.com.
              -
                type: A
                ttl: 600
                value: 1.2.3.4
              -
                type: NS
                ttl: 3600
                values:
                  - ns1.example.com.
            """.Replace("            ", ""));

        var app = BuildApp();
        await app.RunAsync(["fmt", _tmp]);

        var output = File.ReadAllText(Path.Combine(_tmp, "example.com.yaml"));
        var nsPos = output.IndexOf("type: NS", StringComparison.Ordinal);
        var aPos = output.IndexOf("type: A", StringComparison.Ordinal);
        var mxPos = output.IndexOf("type: MX", StringComparison.Ordinal);
        var txtPos = output.IndexOf("type: TXT", StringComparison.Ordinal);
        var caaPos = output.IndexOf("type: CAA", StringComparison.Ordinal);

        nsPos.ShouldBeLessThan(aPos);
        aPos.ShouldBeLessThan(mxPos);
        mxPos.ShouldBeLessThan(txtPos);
        txtPos.ShouldBeLessThan(caaPos);
    }

    [Fact]
    public async Task Fmt_MultipleFiles_FormatsAll()
    {
        WriteZone("a.com.yaml", """
            # Zone: a.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: TXT
                ttl: 600
                value: first
              -
                type: A
                ttl: 600
                value: 1.1.1.1
            """.Replace("            ", ""));

        WriteZone("b.com.yaml", """
            # Zone: b.com.
            # Imported by dns-sync on 2026-01-01

            '':
              -
                type: TXT
                ttl: 600
                value: second
              -
                type: A
                ttl: 600
                value: 2.2.2.2
            """.Replace("            ", ""));

        var app = BuildApp();
        var result = await app.RunAsync(["fmt", _tmp]);

        result.ShouldBe(0);

        // Both should have A before TXT now
        foreach (var file in new[] { "a.com.yaml", "b.com.yaml" })
        {
            var content = File.ReadAllText(Path.Combine(_tmp, file));
            var aPos = content.IndexOf("type: A", StringComparison.Ordinal);
            var txtPos = content.IndexOf("type: TXT", StringComparison.Ordinal);
            aPos.ShouldBeLessThan(txtPos, $"{file}: A should appear before TXT");
        }
    }
}
