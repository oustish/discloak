namespace Bdxl;

/// <summary>One copy of a file's content on a disc (or an alias pointing at a copy stored under another path).</summary>
public class BackupRef
{
    public string Disc { get; set; } = "";
    public string DiscPath { get; set; } = "";     // path of the stored file on the disc (ends in .enc when encrypted)
    public string Sha256 { get; set; } = "";       // SHA-256 of the PLAINTEXT content this copy represents
    public DateTime? BackedUpUtc { get; set; }     // null until the disc is marked as burned

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unverified { get; set; }           // taken from a manifest / catalog match without re-hashing the disc

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Alias { get; set; }                // identical content stored on that disc under another path (dedup)

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Encrypted { get; set; }            // the stored file is encrypted

    public string? StoredSha256 { get; set; }      // SHA-256 of the stored (encrypted) file; used by 'verify' without a password

    // split files (stage --split): this copy is ONE PART of the content; Sha256 above stays the hash of the whole file
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Part { get; set; }                  // 1-based; 0 = the whole file
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Parts { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Offset { get; set; }               // where the part starts in the whole file
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Length { get; set; }               // plaintext bytes in the part
    public string? PartSha256 { get; set; }        // SHA-256 of the part's plaintext

    [JsonIgnore] public bool IsPart => Parts > 0;
    [JsonIgnore] public string ContentSha256 => PartSha256 ?? Sha256;   // plaintext hash of what the stored file holds

    public BackupRef AsAlias()
    {
        var a = (BackupRef)MemberwiseClone();
        a.Alias = true;
        return a;
    }
}

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

    public List<BackupRef> Backups { get; set; } = new();   // history of every copy ever staged / burned
}

public class DiscRecord
{
    public string Label { get; set; } = "";        // e.g. BDXL-0001 (write it on the disc!)
    public string State { get; set; } = "staged";  // staged | burned | lost | destroyed
    public DateTime CreatedUtc { get; set; }
    public DateTime? BurnedUtc { get; set; }
    public DateTime? LostUtc { get; set; }         // when it was marked lost / destroyed
    public DateTime? RestagedUtc { get; set; }     // last time the disc folder was rebuilt for re-burning
    public int FileCount { get; set; }
    public long UsedBytes { get; set; }
    public string? Note { get; set; }
    public long? Capacity { get; set; }            // size of the disc (if known)
    public long? Reserve { get; set; }             // part of that capacity deliberately left free
    public decimal? Cost { get; set; }             // price paid for this disc (blank, imported or existing)

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Encrypted { get; set; }            // files on this disc were encrypted when staged
}

/// <summary>A blank disc the user owns and registered with 'blanks add'.</summary>
public class BlankDisc
{
    public string Id { get; set; } = "";
    public long Capacity { get; set; }             // bytes
    public long Reserve { get; set; }              // bytes deliberately kept free (filesystem overhead, catalog backup, ...)
    public decimal? Cost { get; set; }             // price of this blank disc
    public DateTime AddedUtc { get; set; }

    [JsonIgnore] public long Usable => Capacity - Reserve;
}

/// <summary>
/// One line of manifest.jsonl on a disc. For encrypted files Sha256/Size describe the stored file. A part of a split
/// file also says which part it is, where it belongs in the whole file, and the whole file's size and hash.
/// </summary>
public record ManifestLine(string Path, long Size, string Sha256, bool Encrypted = false, string? PlainSha256 = null,
                           int? Part = null, int? Parts = null, long? Offset = null, long? Length = null,
                           long? FileSize = null, string? FileSha256 = null)
{
    public static ManifestLine For(BackupRef b, long storedSize, long fileSize) =>
        new(b.DiscPath, storedSize, b.Encrypted ? b.StoredSha256 ?? "" : b.ContentSha256, b.Encrypted,
            b.Encrypted ? b.ContentSha256 : null,
            b.IsPart ? b.Part : null, b.IsPart ? b.Parts : null, b.IsPart ? b.Offset : null, b.IsPart ? b.Length : null,
            b.IsPart ? fileSize : null, b.IsPart ? b.Sha256 : null);
}

/// <summary>disc.json on a disc: lets the disc identify itself.</summary>
public record DiscInfo(string Label, DateTime CreatedUtc, int FileCount, long UsedBytes);

/// <summary>Repair-data header for one catalog file (see Rs).</summary>
public record RepairMeta(string File, long Length, string FileSha256, int BlockSize, int DataBlocks, int ParityBlocks, string[] BlockSha256);

public enum RsStatus { Ok, Repaired, Unrecoverable }

/// <summary>One file to store on a disc: a whole file, or one part of a file split across discs.</summary>
public record Piece(FileRecord File, int Part, int Parts, long Offset, long Length)
{
    public static Piece Whole(FileRecord f) => new(f, 0, 0, 0, f.Size);
    public bool IsPart => Parts > 0;
    public string Name => $"{File.RootName}/{File.RelPath}" + (IsPart ? SplitLayout.Suffix(Part, Parts) : "");
}

/// <summary>What one blank disc would receive.</summary>
public class Bin
{
    public Bin(BlankDisc blank) { Blank = blank; }
    public BlankDisc Blank { get; }
    public long Usable => Blank.Usable;
    public List<Piece> Pieces { get; } = new();
    public long Used { get; private set; }
    public long Free => Usable - Used;
    public void Add(Piece p, long storedSize) { Pieces.Add(p); Used += storedSize; }
}

/// <summary>Pending = everything still to be stored (parts of a split file are separate pieces). TooBig = whole files
/// that fit on no blank disc and were not split (no --split).</summary>
public record PlanResult(List<Bin> Bins, List<Piece> Unplaced, List<FileRecord> TooBig, List<Piece> Pending)
{
    public int PendingFiles => Pending.Select(p => p.File).Distinct().Count();
    public long PendingBytes => Pending.Sum(p => p.Length);
}

/// <summary>A file found on an already burned disc by 'scandisc'. Sha256 is the hash of the whole file, also for a part.</summary>
public record DiscFile(string Path, long Size, DateTime Mtime, string? Sha256, bool Unverified, bool Encrypted = false, string? StoredSha256 = null)
{
    public ManifestLine? SplitLine { get; init; }   // set when the manifest says this is one part of a split file
}

public sealed class UserException : Exception
{
    public UserException(string message) : base(message) { }
}
