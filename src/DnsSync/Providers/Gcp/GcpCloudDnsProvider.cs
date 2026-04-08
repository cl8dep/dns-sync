using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnsSync.Core;
using DnsSync.Core.Records;
using Microsoft.Extensions.Logging;

namespace DnsSync.Providers.Gcp;

/// <summary>
/// Google Cloud DNS provider using the Cloud DNS REST API v1.
/// Authenticates via a service account credentials file (or GOOGLE_APPLICATION_CREDENTIALS),
/// falling back to the GCE metadata server for ADC when running on GCP infrastructure.
/// </summary>
public class GcpCloudDnsProvider : IProvider
{
    private readonly string _project;
    private readonly bool? _privateZones;   // null=all, true=private only, false=public only
    private readonly ILogger<GcpCloudDnsProvider> _logger;
    private readonly HttpClient _http;

    // Service account credentials (null when using metadata server ADC)
    private readonly string? _serviceAccountEmail;
    private readonly string? _privateKeyPem;

    // Cached access token
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    private const string BaseUrl = "https://dns.googleapis.com/dns/v1";
    private const string Scope = "https://www.googleapis.com/auth/ndev.clouddns.readwrite";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GcpCloudDnsProvider(
        string? project,
        string? credentialsFile,
        bool? privateZones,
        ILogger<GcpCloudDnsProvider> logger)
    {
        _logger = logger;
        _privateZones = privateZones;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Resolve credentials file: explicit config > GOOGLE_APPLICATION_CREDENTIALS env var
        var resolvedCreds = credentialsFile is not null
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(credentialsFile))
            : Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

        if (resolvedCreds is not null && File.Exists(resolvedCreds))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(resolvedCreds));
            var root = doc.RootElement;

            _serviceAccountEmail = root.TryGetProperty("client_email", out var e) ? e.GetString() : null;
            _privateKeyPem = root.TryGetProperty("private_key", out var k) ? k.GetString() : null;

            // Derive project from the credentials file if not explicitly set in config
            if (string.IsNullOrEmpty(project) && root.TryGetProperty("project_id", out var pid))
                project = pid.GetString();
        }
        else if (resolvedCreds is not null)
        {
            _logger.LogWarning("Credentials file not found: {Path}. Falling back to metadata server ADC.", resolvedCreds);
        }

        // Fall back to well-known project env vars
        if (string.IsNullOrEmpty(project))
            project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
                   ?? Environment.GetEnvironmentVariable("GCLOUD_PROJECT")
                   ?? Environment.GetEnvironmentVariable("CLOUDSDK_CORE_PROJECT");

        if (string.IsNullOrEmpty(project))
            throw new InvalidOperationException(
                "GCP project is required. Set 'project' in config, include 'project_id' in the credentials file, " +
                "or set the GOOGLE_CLOUD_PROJECT environment variable.");

        _project = project;
    }

    // ─── IProvider ────────────────────────────────────────────────────────────

    public async Task PreflightAsync(CancellationToken ct = default)
    {
        // List one zone to verify credentials and project access
        await GetAsync($"/projects/{_project}/managedZones?maxResults=1", ct);
        _logger.LogInformation("GCP Cloud DNS access verified for project {Project}", _project);
    }

    public async Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default)
    {
        var normalized = NormalizeZoneName(zoneName);
        var managedZone = await ResolveZoneResourceAsync(normalized, ct);
        var records = await GetAllRrsetsAsync(managedZone, normalized, ct);
        return new DnsZone { Name = normalized, Records = records };
    }

    public async Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default)
    {
        var zones = new List<string>();
        string? pageToken = null;

        do
        {
            var url = $"/projects/{_project}/managedZones?maxResults=200"
                    + (pageToken is not null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");

            var json = await GetAsync(url, ct);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("managedZones", out var list))
            {
                foreach (var z in list.EnumerateArray())
                {
                    if (!MatchesVisibilityFilter(z)) continue;
                    var dnsName = z.TryGetProperty("dnsName", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrEmpty(dnsName))
                        zones.Add(NormalizeZoneName(dnsName));
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var pt)
                ? pt.GetString() : null;
        } while (pageToken is not null);

        return zones;
    }

    public async Task<ApplyResult> ApplyPlanAsync(string zoneName, DnsPlan plan, CancellationToken ct = default)
    {
        if (plan.IsEmpty) return new ApplyResult(0, 0, []);

        var normalized = NormalizeZoneName(zoneName);
        var managedZone = await ResolveZoneResourceAsync(normalized, ct);

        var additions = new List<object>();
        var deletions = new List<object>();

        foreach (var change in plan.Changes)
        {
            switch (change.ChangeType)
            {
                case ChangeType.Create:
                    additions.Add(ToGcpRrset(change.After!));
                    break;

                case ChangeType.Delete:
                    deletions.Add(ToGcpRrset(change.Before!));
                    break;

                case ChangeType.Update:
                    // GCP Changes API: atomically delete old + add new
                    deletions.Add(ToGcpRrset(change.Before!));
                    additions.Add(ToGcpRrset(change.After!));
                    break;
            }
        }

        var body = JsonSerializer.Serialize(new { additions, deletions }, JsonOpts);
        _logger.LogDebug("Submitting GCP DNS change: {Additions} additions, {Deletions} deletions",
            additions.Count, deletions.Count);

        await PostAsync($"/projects/{_project}/managedZones/{managedZone}/changes", body, ct);

        _logger.LogInformation("Applied {Count} DNS change(s) to zone {Zone} in project {Project}",
            plan.Changes.Count, zoneName, _project);

        return new ApplyResult(plan.Changes.Count, 0, []);
    }

    // ─── Zone resolution ──────────────────────────────────────────────────────

    private async Task<string> ResolveZoneResourceAsync(string zoneName, CancellationToken ct)
    {
        var dnsName = Uri.EscapeDataString(zoneName);
        var json = await GetAsync($"/projects/{_project}/managedZones?dnsName={dnsName}", ct);
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("managedZones", out var list))
            throw new InvalidOperationException(
                $"Zone '{zoneName}' not found in GCP project '{_project}'.");

        foreach (var z in list.EnumerateArray())
        {
            if (!MatchesVisibilityFilter(z)) continue;
            if (z.TryGetProperty("name", out var n))
                return n.GetString()!;
        }

        var filterDesc = _privateZones switch
        {
            true => " (private zones only)",
            false => " (public zones only)",
            null => ""
        };
        throw new InvalidOperationException(
            $"Zone '{zoneName}' not found in GCP project '{_project}'{filterDesc}.");
    }

    private bool MatchesVisibilityFilter(JsonElement zone)
    {
        if (_privateZones is null) return true;

        var visibility = zone.TryGetProperty("visibility", out var v) ? v.GetString() : "public";
        return _privateZones.Value
            ? string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase)
            : string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase);
    }

    // ─── Record parsing ───────────────────────────────────────────────────────

    private async Task<IReadOnlyList<DnsRecord>> GetAllRrsetsAsync(
        string managedZone, string zoneName, CancellationToken ct)
    {
        var records = new List<DnsRecord>();
        string? pageToken = null;

        do
        {
            var url = $"/projects/{_project}/managedZones/{managedZone}/rrsets?maxResults=500"
                    + (pageToken is not null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");

            var json = await GetAsync(url, ct);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("rrsets", out var list))
            {
                foreach (var r in list.EnumerateArray())
                {
                    var record = ParseGcpRrset(r, zoneName);
                    if (record is not null) records.Add(record);
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var pt)
                ? pt.GetString() : null;
        } while (pageToken is not null);

        return records;
    }

    private static DnsRecord? ParseGcpRrset(JsonElement r, string zoneName)
    {
        var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var type = r.TryGetProperty("type", out var t) ? t.GetString()?.ToUpperInvariant() ?? "" : "";
        var ttl = r.TryGetProperty("ttl", out var ttlEl) ? ttlEl.GetInt32() : 3600;

        if (!r.TryGetProperty("rrdatas", out var rrdatas)) return null;

        var values = rrdatas.EnumerateArray()
            .Select(v => v.GetString() ?? "")
            .Where(v => v.Length > 0)
            .ToList();

        if (values.Count == 0) return null;

        var fqdn = NormalizeFqdn(name);

        return type switch
        {
            "A" => new ARecord { Name = fqdn, Type = "A", Ttl = ttl, Addresses = values },

            "AAAA" => new AaaaRecord { Name = fqdn, Type = "AAAA", Ttl = ttl, Addresses = values },

            "CNAME" => new CnameRecord
            {
                Name = fqdn, Type = "CNAME", Ttl = ttl,
                Target = NormalizeFqdn(values[0])
            },

            "MX" => new MxRecord
            {
                Name = fqdn, Type = "MX", Ttl = ttl,
                Values = values.Select(ParseMxRrdata).ToList()
            },

            "TXT" => new TxtRecord
            {
                Name = fqdn, Type = "TXT", Ttl = ttl,
                Values = values.Select(UnquoteTxt).ToList()
            },

            "NS" => new NsRecord
            {
                Name = fqdn, Type = "NS", Ttl = ttl,
                Nameservers = values.Select(NormalizeFqdn).ToList()
            },

            "CAA" => new CaaRecord
            {
                Name = fqdn, Type = "CAA", Ttl = ttl,
                Values = values.Select(ParseCaaRrdata).ToList()
            },

            "SRV" => new SrvRecord
            {
                Name = fqdn, Type = "SRV", Ttl = ttl,
                Values = values.Select(ParseSrvRrdata).ToList()
            },

            _ => null
        };
    }

    private static MxValue ParseMxRrdata(string v)
    {
        var idx = v.IndexOf(' ');
        if (idx < 0) return new MxValue(10, NormalizeFqdn(v));
        return new MxValue(
            int.TryParse(v[..idx], out var pref) ? pref : 10,
            NormalizeFqdn(v[(idx + 1)..]));
    }

    private static CaaValue ParseCaaRrdata(string v)
    {
        // Format: "flags tag \"value\"" or "flags tag value"
        var parts = v.Split(' ', 3);
        if (parts.Length < 3) return new CaaValue(0, parts.ElementAtOrDefault(1) ?? "", "");
        var val = parts[2].Trim('"');
        return new CaaValue(
            int.TryParse(parts[0], out var flags) ? flags : 0,
            parts[1],
            val);
    }

    private static SrvValue ParseSrvRrdata(string v)
    {
        var parts = v.Split(' ', 4);
        return new SrvValue(
            int.TryParse(parts.ElementAtOrDefault(0), out var pri) ? pri : 0,
            int.TryParse(parts.ElementAtOrDefault(1), out var wt) ? wt : 0,
            int.TryParse(parts.ElementAtOrDefault(2), out var port) ? port : 0,
            NormalizeFqdn(parts.ElementAtOrDefault(3) ?? ""));
    }

    /// <summary>
    /// GCP returns TXT rrdatas as quoted strings: "\"actual content\""
    /// Strip the outer quotes and unescape inner ones.
    /// </summary>
    private static string UnquoteTxt(string value)
    {
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
            return value[1..^1].Replace("\\\"", "\"");
        return value;
    }

    // ─── GCP payload builders ─────────────────────────────────────────────────

    private static object ToGcpRrset(DnsRecord record) => new
    {
        name = record.Name,
        type = record.Type,
        ttl = record.Ttl,
        rrdatas = ToRrdatas(record)
    };

    private static List<string> ToRrdatas(DnsRecord record) => record switch
    {
        ARecord a => [.. a.Addresses],
        AaaaRecord aaaa => [.. aaaa.Addresses],
        CnameRecord cname => [cname.Target],
        MxRecord mx => mx.Values.Select(v => $"{v.Preference} {v.Exchange}").ToList(),
        TxtRecord txt => txt.Values.Select(QuoteTxt).ToList(),
        NsRecord ns => [.. ns.Nameservers],
        CaaRecord caa => caa.Values.Select(v => $"{v.Flags} {v.Tag} \"{v.Value}\"").ToList(),
        SrvRecord srv => srv.Values.Select(v => $"{v.Priority} {v.Weight} {v.Port} {v.Target}").ToList(),
        _ => throw new NotSupportedException($"Record type {record.Type} not supported for GCP DNS")
    };

    private static string QuoteTxt(string value)
    {
        // GCP expects TXT values as quoted strings with inner quotes escaped
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    // ─── Authentication ───────────────────────────────────────────────────────

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // Return cached token if still valid (5 minute buffer before expiry)
        if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry - TimeSpan.FromMinutes(5))
            return _accessToken;

        if (_serviceAccountEmail is not null && _privateKeyPem is not null)
        {
            _accessToken = await GetServiceAccountTokenAsync(ct);
        }
        else
        {
            _accessToken = await GetMetadataTokenAsync(ct);
        }

        return _accessToken;
    }

    private async Task<string> GetServiceAccountTokenAsync(CancellationToken ct)
    {
        var jwt = CreateServiceAccountJwt(_serviceAccountEmail!, _privateKeyPem!, Scope);

        var resp = await _http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new KeyValuePair<string, string>("assertion", jwt)
            ]), ct);

        resp.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        _logger.LogDebug("Obtained GCP access token via service account (expires in {Seconds}s)", expiresIn);
        return token;
    }

    private async Task<string> GetMetadataTokenAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token");
        req.Headers.Add("Metadata-Flavor", "Google");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "GCP metadata server not reachable and no credentials file was provided. " +
                "Set GOOGLE_APPLICATION_CREDENTIALS or configure 'credentials_file' in config.", ex);
        }

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        _logger.LogDebug("Obtained GCP access token via metadata server ADC (expires in {Seconds}s)", expiresIn);
        return token;
    }

    /// <summary>
    /// Build a signed JWT for service account authentication (RS256).
    /// No external dependencies — uses System.Security.Cryptography.
    /// </summary>
    private static string CreateServiceAccountJwt(string email, string privateKeyPem, string scope)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new { alg = "RS256", typ = "JWT" }));

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = email,
            scope,
            aud = "https://oauth2.googleapis.com/token",
            exp = now + 3600,
            iat = now
        }));

        var signingInput = Encoding.UTF8.GetBytes($"{header}.{payload}");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{header}.{payload}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ─── HTTP helpers ─────────────────────────────────────────────────────────

    private async Task<string> GetAsync(string path, CancellationToken ct) =>
        await RetryAsync(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await GetAccessTokenAsync(ct));
            var resp = await _http.SendAsync(req, ct);
            return await EnsureSuccessAsync(resp);
        }, ct);

    private async Task<string> PostAsync(string path, string json, CancellationToken ct) =>
        await RetryAsync(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await GetAccessTokenAsync(ct));
            var resp = await _http.SendAsync(req, ct);
            return await EnsureSuccessAsync(resp);
        }, ct);

    private static async Task<string> EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadAsStringAsync();

        var body = await resp.Content.ReadAsStringAsync();
        string? message = null;
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch { /* ignore parse errors */ }

        throw new HttpRequestException(
            $"GCP DNS API error ({(int)resp.StatusCode}): {message ?? body}",
            null, resp.StatusCode);
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct, int maxRetries = 3)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (IsTransient(ex))
            {
                _logger.LogWarning("Transient GCP error on attempt {Attempt}/{Max}: {Error}. Retrying in {Delay}ms",
                    attempt + 1, maxRetries, ex.Message, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }
        return await action();
    }

    private static bool IsTransient(HttpRequestException ex) => ex.StatusCode is
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout or
        HttpStatusCode.RequestTimeout;

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string NormalizeZoneName(string name)
    {
        var lower = name.ToLowerInvariant().Trim();
        return lower.EndsWith('.') ? lower : lower + ".";
    }

    private static string NormalizeFqdn(string value)
    {
        var lower = value.ToLowerInvariant().Trim();
        return lower.EndsWith('.') ? lower : lower + ".";
    }
}
