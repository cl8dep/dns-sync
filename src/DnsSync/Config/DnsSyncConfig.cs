using YamlDotNet.Serialization;

namespace DnsSync.Config;

public class DnsSyncConfig
{
    [YamlMember(Alias = "providers")]
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();

    [YamlMember(Alias = "zones")]
    public Dictionary<string, ZoneConfig> Zones { get; set; } = new();
}

public class ProviderConfig
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    // yaml provider
    [YamlMember(Alias = "directory")]
    public string? Directory { get; set; }

    // cloudflare provider
    [YamlMember(Alias = "api_token")]
    public string? ApiToken { get; set; }

    [YamlMember(Alias = "account_id")]
    public string? AccountId { get; set; }

    // route53 (future)
    [YamlMember(Alias = "access_key_id")]
    public string? AccessKeyId { get; set; }

    [YamlMember(Alias = "secret_access_key")]
    public string? SecretAccessKey { get; set; }

    [YamlMember(Alias = "region")]
    public string? Region { get; set; }

    // gcp_cloud_dns provider
    [YamlMember(Alias = "project")]
    public string? Project { get; set; }

    [YamlMember(Alias = "credentials_file")]
    public string? CredentialsFile { get; set; }

    /// <summary>
    /// Restrict zone lookups by visibility.
    /// true = private zones only, false = public zones only, null = no restriction.
    /// </summary>
    [YamlMember(Alias = "private")]
    public bool? Private { get; set; }
}

public class ZoneConfig
{
    [YamlMember(Alias = "source")]
    public string Source { get; set; } = string.Empty;

    [YamlMember(Alias = "targets")]
    public List<string> Targets { get; set; } = new();
}
