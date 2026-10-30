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
    public long? StoredSize { get; set; }          // bytes of the stored file on the disc (the manifest lists it)

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Compression { get; set; }           // zstd level the stored copy was made with (0 = none); restage uses it again

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
