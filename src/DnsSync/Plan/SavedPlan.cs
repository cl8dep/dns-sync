namespace DnsSync.Plan;

public class SavedPlanFile
{
    public SavedPlanBody Plan { get; set; } = new();
    public string Signature { get; set; } = string.Empty;  // "sha256:<hex>"
}

public class SavedPlanBody
{
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public string ConfigHash { get; set; } = string.Empty;  // "sha256:<hex>"
    public List<SavedZonePlan> Zones { get; set; } = [];
}

public class SavedZonePlan
{
    public string Zone { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public List<SavedChange> Changes { get; set; } = [];
}

public class SavedChange
{
    public string Type { get; set; } = string.Empty;       // "create", "update", "delete"
    public string Name { get; set; } = string.Empty;
    public string RecordType { get; set; } = string.Empty;
    public SavedRecord? Before { get; set; }
    public SavedRecord? After { get; set; }
}

/// <summary>
/// Record snapshot in a saved plan. Values are encoded as strings, with type-specific conventions:
/// A/AAAA/NS: one address per entry.
/// CNAME: single target FQDN.
/// TXT: one TXT string per entry (unquoted).
/// MX: "priority exchange" (e.g. "10 mail.example.com.").
/// SRV: "priority weight port target" (e.g. "10 20 443 sip.example.com.").
/// CAA: "flags tag value" (e.g. "0 issue letsencrypt.org").
/// </summary>
public class SavedRecord
{
    public int Ttl { get; set; }
    public List<string> Values { get; set; } = [];
}
