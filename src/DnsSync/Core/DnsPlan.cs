namespace DnsSync.Core;

public class DnsPlan
{
    public required IReadOnlyList<RecordChange> Changes { get; init; }

    public int Creates => Changes.Count(c => c.ChangeType == ChangeType.Create);
    public int Updates => Changes.Count(c => c.ChangeType == ChangeType.Update);
    public int Deletes => Changes.Count(c => c.ChangeType == ChangeType.Delete);
    public int Total => Changes.Count;
    public bool IsEmpty => Changes.Count == 0;
}
