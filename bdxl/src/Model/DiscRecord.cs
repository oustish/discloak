namespace Bdxl;

public class DiscRecord
{
    public string Label { get; set; } = "";        // e.g. BDXL-0001 (write it on the disc!)
    public string State { get; set; } = "staged";  // staged | burned | lost | destroyed
    public DateTime CreatedUtc { get; set; }
    public DateTime? BurnedUtc { get; set; }
    public DateTime? LostUtc { get; set; }         // when it was marked lost / destroyed
    public DateTime? RestagedUtc { get; set; }     // last time the disc folder was rebuilt for re-burning
    public int FileCount { get; set; }             // stored copies on it (kept in the catalog and the encrypted manifest only)
    public long UsedBytes { get; set; }            // their stored bytes
    public string? Note { get; set; }
    public long? Capacity { get; set; }            // size of the disc (if known)
    public long? Reserve { get; set; }             // part of that capacity deliberately left free
    public decimal? Cost { get; set; }             // price paid for this disc (blank, imported or existing)

    public string? KeyId { get; set; }             // id of the master key its packs and manifests are encrypted with
    public List<string> Packs { get; set; } = new();   // SHA-256 of each pack file (pack n at index n - 1): 'verify' without a password
    public List<string> Manifests { get; set; } = new();   // SHA-256 of each session's manifest file (session n at index n - 1, "" = not known)

    public int Sessions { get; set; } = 1;         // sessions burned (or staged) onto the disc: 'stage --append' adds one
    public long OccupiedBytes { get; set; }        // space all sessions take: packs + per session max(reserve, metadata)
    public AppendSession? PendingAppend { get; set; }   // a session staged with 'stage --append', not burned yet

    /// <summary>Records the hash of session `session`'s manifest; the hashes of later sessions are dropped.</summary>
    public void SetManifest(int session, string sha256)
    {
        if (Manifests.Count >= session) Manifests.RemoveRange(session - 1, Manifests.Count - session + 1);
        while (Manifests.Count < session - 1) Manifests.Add("");   // sessions staged before the hashes were kept
        Manifests.Add(sha256);
    }
}
