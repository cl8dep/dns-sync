using DnsSync.Config;
using DnsSync.Providers.Cloudflare;
using DnsSync.Providers.Yaml;
using Microsoft.Extensions.Logging;

namespace DnsSync.Providers;

public static class ProviderFactory
{
    public static IProvider Create(string name, ProviderConfig config, ILoggerFactory loggerFactory)
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

            _ => throw new NotSupportedException(
                $"Provider type '{config.Type}' is not supported in this build. " +
                "Supported types: yaml, cloudflare")
        };
    }
}
