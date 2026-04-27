using DnsSync.Config;
using DnsSync.Providers.Cloudflare;
using DnsSync.Providers.Gcp;
using DnsSync.Providers.GoDaddy;
using DnsSync.Providers.Porkbun;
using DnsSync.Providers.Route53;
using DnsSync.Providers.Yaml;
using Microsoft.Extensions.Logging;

namespace DnsSync.Providers;

public static class ProviderFactory
{
    public static IProvider Create(string name, ProviderConfig config, ILoggerFactory loggerFactory)
    {
        var provider = CreateInner(name, config, loggerFactory);
        return config.ReadOnly ? new ReadOnlyProviderGuard(provider, name) : provider;
    }

    private static IProvider CreateInner(string name, ProviderConfig config, ILoggerFactory loggerFactory)
    {
        return config.Type.ToLowerInvariant() switch
        {
            "yaml" => new YamlProvider(
                config.Directory ?? throw new InvalidOperationException(
                    $"Provider '{name}' (yaml) requires 'directory'.")),

            "cloudflare" => new CloudflareProvider(
                config.ApiToken ?? throw new InvalidOperationException(
                    $"Provider '{name}' (cloudflare) requires 'api_token'."),
                loggerFactory.CreateLogger<CloudflareProvider>(),
                config.AccountId),

            "gcp_cloud_dns" => new GcpCloudDnsProvider(
                config.Project,
                config.CredentialsFile,
                config.Private,
                loggerFactory.CreateLogger<GcpCloudDnsProvider>()),

            "porkbun" => new PorkbunProvider(
                config.ApiKey ?? throw new InvalidOperationException(
                    $"Provider '{name}' (porkbun) requires 'api_key'."),
                config.SecretKey ?? throw new InvalidOperationException(
                    $"Provider '{name}' (porkbun) requires 'secret_key'."),
                loggerFactory.CreateLogger<PorkbunProvider>()),

            "route53" => new Route53Provider(
                config.AccessKeyId
                    ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")
                    ?? throw new InvalidOperationException(
                        $"Provider '{name}' (route53) requires 'access_key_id' or AWS_ACCESS_KEY_ID env var."),
                config.SecretAccessKey
                    ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")
                    ?? throw new InvalidOperationException(
                        $"Provider '{name}' (route53) requires 'secret_access_key' or AWS_SECRET_ACCESS_KEY env var."),
                config.Region ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-east-1",
                loggerFactory.CreateLogger<Route53Provider>(),
                sessionToken: Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"),
                hostedZoneId: config.HostedZoneId),

            "godaddy" => new GoDaddyProvider(
                config.ApiKey ?? throw new InvalidOperationException(
                    $"Provider '{name}' (godaddy) requires 'api_key'."),
                config.SecretKey ?? throw new InvalidOperationException(
                    $"Provider '{name}' (godaddy) requires 'secret_key'."),
                loggerFactory.CreateLogger<GoDaddyProvider>()),

            _ => throw new NotSupportedException(
                $"Provider type '{config.Type}' is not supported in this build. " +
                "Supported types: yaml, cloudflare, gcp_cloud_dns, porkbun, route53, godaddy")
        };
    }
}
