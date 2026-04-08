using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DnsSync.Config;

public static class ConfigLoader
{
    private static readonly Regex EnvVarPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownProviderTypes =
        new(StringComparer.OrdinalIgnoreCase) { "yaml", "cloudflare", "gcp_cloud_dns", "route53" };

    public static DnsSyncConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");

        var raw = File.ReadAllText(path);
        var interpolated = InterpolateEnvVars(raw);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<DnsSyncConfig>(interpolated)
               ?? throw new InvalidOperationException("Config file is empty or invalid.");

        // Resolve relative paths in provider configs against the config file's directory,
        // not the current working directory. This lets users run dns-sync from any folder.
        var configDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var provider in config.Providers.Values)
        {
            if (!string.IsNullOrEmpty(provider.Directory) && !Path.IsPathRooted(provider.Directory))
                provider.Directory = Path.GetFullPath(Path.Combine(configDir, provider.Directory));
        }

        return config;
    }

    public static string InterpolateEnvVars(string input)
    {
        var lines = input.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#'))
                continue;

            lines[i] = EnvVarPattern.Replace(lines[i], match =>
            {
                var varName = match.Groups[1].Value;
                var value = Environment.GetEnvironmentVariable(varName);
                if (value is null)
                    throw new InvalidOperationException(
                        $"Environment variable '{varName}' is referenced in config but not set.");
                return value;
            });
        }

        return string.Join('\n', lines);
    }

    public static List<string> ValidateStructure(DnsSyncConfig config)
    {
        var errors = new List<string>();

        if (config.Providers.Count == 0)
            errors.Add("No providers defined in config.");

        if (config.Zones.Count == 0)
            errors.Add("No zones defined in config.");

        foreach (var (name, provider) in config.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Type))
            {
                errors.Add($"Provider '{name}' is missing 'type'.");
                continue;
            }

            if (!KnownProviderTypes.Contains(provider.Type))
                errors.Add($"Provider '{name}' has unknown type '{provider.Type}'. " +
                           $"Known types: {string.Join(", ", KnownProviderTypes)}");

            switch (provider.Type.ToLowerInvariant())
            {
                case "yaml":
                    if (string.IsNullOrWhiteSpace(provider.Directory))
                        errors.Add($"Provider '{name}' (yaml) requires 'directory'.");
                    break;
                case "cloudflare":
                    if (string.IsNullOrWhiteSpace(provider.ApiToken))
                        errors.Add($"Provider '{name}' (cloudflare) requires 'api_token'.");
                    break;

                case "gcp_cloud_dns":
                    // project is optional — can come from credentials file or env vars at runtime
                    // credentials_file is optional — falls back to GOOGLE_APPLICATION_CREDENTIALS or ADC
                    break;
            }
        }

        foreach (var (zoneName, zone) in config.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Source))
                errors.Add($"Zone '{zoneName}' has no source provider.");
            else if (!config.Providers.ContainsKey(zone.Source))
                errors.Add($"Zone '{zoneName}' references unknown source provider '{zone.Source}'.");

            if (zone.Targets.Count == 0)
                errors.Add($"Zone '{zoneName}' has no target providers.");

            foreach (var target in zone.Targets)
            {
                if (!config.Providers.ContainsKey(target))
                    errors.Add($"Zone '{zoneName}' references unknown target provider '{target}'.");

                if (target == zone.Source)
                    errors.Add($"Zone '{zoneName}' uses '{target}' as both source and target — this would overwrite source data.");
            }
        }

        return errors;
    }
}
