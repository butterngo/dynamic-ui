namespace DynamicUi.Server.Data;

/// <summary>One persisted, immutable snapshot of the whole UI schema tree.</summary>
public class SchemaVersion
{
    public int Version { get; set; }          // monotonic, primary key
    public string Json { get; set; } = "{}";  // the full UI tree at this version
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Audit row: the RFC-6902 patch (or rollback marker) that produced a version.</summary>
public class PatchEntry
{
    public int Id { get; set; }
    public int Version { get; set; }            // the version this patch produced
    public string Op { get; set; } = "[]";      // the JSON Patch document, or a rollback note
    public DateTimeOffset CreatedAt { get; set; }
}
