namespace Discloak;

/// <summary>
/// One copy of a file's content on a disc (or an alias pointing at a copy stored for another file): StoredSize bytes
/// from position At of the disc's data stream (see DiscFormat), as they are or compressed.
/// </summary>
public class BackupRef
{
    public string Disc { get; set; } = "";
    public long At { get; set; }                   // where the stored copy starts in the disc's data stream
    public long StoredSize { get; set; }           // its bytes there (compressed, or the plain size)
    public string Sha256 { get; set; } = "";       // SHA-256 of the PLAINTEXT content this copy represents
    public DateTime? BackedUpUtc { get; set; }     // null until the disc is marked as burned

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unverified { get; set; }           // taken from the disc's manifest (scandisc) and not checked yet by 'verify'

    // 'verify' found this copy damaged (since 0.10.0; older versions ignore both): it no longer counts as a copy - the
    // file is pending again while on its source - but restore still tries it last. The next verify that reads it back
    // intact, or a restage of the disc, removes the mark.
    public DateTime? DamagedUtc { get; set; }
    public string? Damage { get; set; }            // what verify found ("pack 312 differs from the catalog")

    [JsonIgnore] public bool IsDamaged => DamagedUtc != null;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Alias { get; set; }                // identical content stored on that disc for another file (dedup)

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Compression { get; set; }           // zstd level the stored copy was made with (0 = none); restage uses it again
    public string? Codec { get; set; }             // what compressed it: "zstd" (null: not compressed, or made before it was recorded = zstd)

    public long Length { get; set; }               // plaintext bytes in the copy (the file's size, or the part's)

    // split files (stage --split): this copy is ONE PART of the content; Sha256 above stays the hash of the whole file
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Part { get; set; }                  // 1-based; 0 = the whole file
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Parts { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Offset { get; set; }               // where the part starts in the whole file
    public string? PartSha256 { get; set; }        // SHA-256 of the part's plaintext

    [JsonIgnore] public bool IsPart => Parts > 0;
    [JsonIgnore] public string ContentSha256 => PartSha256 ?? Sha256;   // plaintext hash of what the stored copy holds
    [JsonIgnore] public long End => At + StoredSize;

    public BackupRef AsAlias()
    {
        var a = (BackupRef)MemberwiseClone();
        a.Alias = true;
        return a;
    }
}
