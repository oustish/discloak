namespace Bdxl;

public class FileRecord
{
    public string Root { get; set; } = "";         // full source folder that was scanned
    public string RootName { get; set; } = "";     // top-level folder name used on discs (the source alias)
    public string RelPath { get; set; } = "";      // path relative to Root, '/' separators
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string Sha256 { get; set; } = "";       // hash of the current version; "" = never hashed (ignored files)
    public DateTime AddedUtc { get; set; }
    public DateTime? MissingSinceUtc { get; set; } // set when the file disappeared from the source

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Ignored { get; set; }              // matches an ignore rule: never staged for NEW backups

    public long? CompressedSize { get; set; }      // stored size of an encrypted, compressed copy, counted by scan ...
    public string? CompressedWith { get; set; }    // ... for this setting (Compression.Signature); otherwise not valid

    public List<BackupRef> Backups { get; set; } = new();   // history of every copy ever staged / burned
}
