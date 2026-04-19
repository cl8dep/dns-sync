using DnsSync.Config;
using DnsSync.Providers.Cloudflare;
using DnsSync.Providers.Gcp;
using DnsSync.Providers.Porkbun;
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

            _ => throw new NotSupportedException(
                $"Provider type '{config.Type}' is not supported in this build. " +
                "Supported types: yaml, cloudflare, gcp_cloud_dns, porkbun")
        };
    }
}
