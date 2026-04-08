namespace DnsSync.Config;

public static class DotEnvLoader
{
    /// <summary>
    /// Load variables from a .env file into the process environment.
    /// Existing environment variables are NOT overridden (system env takes precedence).
    ///
    /// Supported syntax:
    ///   KEY=value
    ///   KEY="quoted value"
    ///   KEY='single quoted'
    ///   export KEY=value     (export prefix is stripped)
    ///   # comment lines
    ///   (blank lines ignored)
    /// </summary>
    /// <returns>Number of variables loaded.</returns>
    public static int Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var loaded = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            // Strip optional "export " prefix
            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                line = line["export ".Length..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];

            // Strip inline comments (only outside quotes)
            value = StripInlineComment(value);

            // Strip surrounding quotes
            value = UnquoteValue(value.Trim());

            if (string.IsNullOrEmpty(key)) continue;

            // Don't override variables already set in the environment
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
                loaded++;
            }
        }

        return loaded;
    }

    /// <summary>
    /// Try to load a .env file. Returns false (silently) if the file doesn't exist.
    /// </summary>
    public static bool TryLoad(string path, out int loaded)
    {
        loaded = 0;
        if (!File.Exists(path)) return false;
        loaded = Load(path);
        return true;
    }

    private static string UnquoteValue(string value)
    {
        if (value.Length >= 2)
        {
            if (value.StartsWith('"') && value.EndsWith('"'))
                return value[1..^1].Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t");

            if (value.StartsWith('\'') && value.EndsWith('\''))
                return value[1..^1];
        }
        return value;
    }

    private static string StripInlineComment(string value)
    {
        // Only strip # comments outside of quoted strings
        var inDouble = false;
        var inSingle = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '#' && !inDouble && !inSingle)
                return value[..i].TrimEnd();
        }

        return value;
    }
}
