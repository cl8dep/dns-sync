namespace DnsSync.Core;

public static class ZoneDiff
{
    /// <summary>
    /// Record types that are always managed by the DNS provider and must never be synced.
    /// SOA is auto-generated; syncing it would corrupt the zone serial.
    /// </summary>
    private static readonly HashSet<string> AlwaysIgnored =
        new(StringComparer.OrdinalIgnoreCase) { "SOA" };

    /// <summary>
    /// Produce a plan by comparing source against target.
    /// Records in source but not target  → Create
    /// Records in both but values differ → Update
    /// Records in target but not source  → Delete
    ///
    /// SOA records are always excluded.
    /// Apex NS records (NS at zone root) are excluded by default; pass includeApexNs=true to override.
    /// </summary>
    public static DnsPlan Diff(DnsZone source, DnsZone target, bool includeApexNs = false)
    {
        var changes = new List<RecordChange>();

        var sourceRecords = source.Records
            .Where(r => !ShouldIgnore(r, source.Name, includeApexNs))
            .ToList();

        var targetRecords = target.Records
            .Where(r => !ShouldIgnore(r, source.Name, includeApexNs))
            .ToList();

        // Group by (name, type) — DNS record sets
        var sourceByKey = sourceRecords
            .GroupBy(r => (r.Name, r.Type))
            .ToDictionary(g => g.Key, g => g.ToList());

        var targetByKey = targetRecords
            .GroupBy(r => (r.Name, r.Type))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Records in source but not in target → Create
        foreach (var (key, srcList) in sourceByKey)
        {
            if (!targetByKey.ContainsKey(key))
            {
                foreach (var record in srcList)
                    changes.Add(new RecordChange { ChangeType = ChangeType.Create, After = record });
            }
        }

        // Records in both → compare by CanonicalHash
        foreach (var (key, srcList) in sourceByKey)
        {
            if (!targetByKey.TryGetValue(key, out var tgtList))
                continue;

            // We compare at the RRset level: one source record vs one target record per (name, type)
            // For record types with multiple values (A with multiple IPs), the whole list is one record
            var src = srcList.First();
            var tgt = tgtList.First();

            var valuesChanged = src.CanonicalHash() != tgt.CanonicalHash();
            var ttlChanged = src.Ttl != tgt.Ttl;

            if (valuesChanged || ttlChanged)
                changes.Add(new RecordChange
                {
                    ChangeType = ChangeType.Update,
                    Before = tgt,
                    After = src
                });
        }

        // Records in target but not in source → Delete
        foreach (var (key, tgtList) in targetByKey)
        {
            if (!sourceByKey.ContainsKey(key))
            {
                foreach (var record in tgtList)
                    changes.Add(new RecordChange { ChangeType = ChangeType.Delete, Before = record });
            }
        }

        return new DnsPlan { Changes = changes };
    }

    private static bool ShouldIgnore(DnsRecord record, string zoneName, bool includeApexNs)
    {
        if (AlwaysIgnored.Contains(record.Type))
            return true;

        // Ignore apex NS records unless explicitly requested
        if (!includeApexNs &&
            string.Equals(record.Type, "NS", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Name, zoneName, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
