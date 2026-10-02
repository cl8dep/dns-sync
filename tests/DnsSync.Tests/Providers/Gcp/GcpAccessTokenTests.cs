using DnsSync.Providers.Gcp;
using DnsSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DnsSync.Tests.Providers.Gcp;

/// <summary>
/// Credential resolution through GOOGLE_OAUTH_ACCESS_TOKEN. The environment is passed in
/// as a lookup, so these tests never touch the process environment.
/// </summary>
public class GcpAccessTokenTests
{
    private const string ZonesJson = """{"managedZones":[]}""";

    private static GcpCloudDnsProvider Make(
        FakeHttpHandler handler, Dictionary<string, string?> env, string? project = "test-project", string? credentialsFile = null) =>
        new(project, credentialsFile, privateZones: null, NullLogger<GcpCloudDnsProvider>.Instance,
            handler.CreateClient(), name => env.GetValueOrDefault(name));

    [Fact]
    public async Task AccessTokenEnv_IsSentAsBearer_WithNoTokenExchange()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(ZonesJson);

        await Make(handler, new() { ["GOOGLE_OAUTH_ACCESS_TOKEN"] = "ya29.from-wif" }).PreflightAsync();

        handler.Requests.Count.ShouldBe(1);
        handler.LastRequest.RequestUri!.Host.ShouldBe("dns.googleapis.com");
        handler.LastRequest.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("ya29.from-wif");
    }

    [Fact]
    public async Task AccessTokenEnv_TakesPrecedenceOverACredentialsFile()
    {
        var creds = Path.GetTempFileName();
        try
        {
            File.WriteAllText(creds,
                """{"type":"authorized_user","client_id":"id","client_secret":"secret","refresh_token":"refresh"}""");
            var handler = new FakeHttpHandler();
            handler.Enqueue(ZonesJson);

            await Make(handler, new() { ["GOOGLE_OAUTH_ACCESS_TOKEN"] = "ya29.from-wif" }, credentialsFile: creds)
                .PreflightAsync();

            // A refresh-token exchange would have gone to oauth2.googleapis.com first.
            handler.Requests.Count.ShouldBe(1);
            handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("ya29.from-wif");
        }
        finally
        {
            File.Delete(creds);
        }
    }

    [Fact]
    public void ProjectFallsBackToEnv_WhenOnlyATokenIsGiven()
    {
        var env = new Dictionary<string, string?>
        {
            ["GOOGLE_OAUTH_ACCESS_TOKEN"] = "ya29.from-wif",
            ["GOOGLE_CLOUD_PROJECT"] = "env-project",
        };

        Should.NotThrow(() => Make(new FakeHttpHandler(), env, project: null));
    }

    [Fact]
    public void EmptyAccessTokenEnv_IsIgnored()
    {
        // No token, no file, no project: the existing "project is required" error, not a
        // silent empty bearer token.
        var env = new Dictionary<string, string?>
        {
            ["GOOGLE_OAUTH_ACCESS_TOKEN"] = "",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = Path.Combine(Path.GetTempPath(), "does-not-exist.json"),
        };

        Should.Throw<InvalidOperationException>(() => Make(new FakeHttpHandler(), env, project: null))
            .Message.ShouldContain("GCP project is required");
    }
}
