namespace Discloak;

public class DiscRecord
{
    public string Label { get; set; } = "";        // e.g. DISC-0001 (write it on the disc!)
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
    public List<string> WrittenBy { get; set; } = new();   // the discloak build that staged each session (BuildInfo.CreatedBy; "" = not known)

    public int Sessions { get; set; } = 1;         // sessions burned (or staged) onto the disc: 'stage --append' adds one
    public long OccupiedBytes { get; set; }        // space all sessions take: packs + per session max(reserve, metadata)
    public AppendSession? PendingAppend { get; set; }   // a session staged with 'stage --append', not burned yet

    /// <summary>Records session `session`: its manifest's hash and the build that wrote it; later sessions are dropped.</summary>
    public void SetSession(int session, string manifestSha256, string writtenBy)
    {
        Set(Manifests, manifestSha256);
        Set(WrittenBy, writtenBy);
        void Set(List<string> list, string value)
        {
            DropSessions(list, session);
            while (list.Count < session - 1) list.Add("");   // sessions staged before this was kept
            list.Add(value);
        }
    }

    /// <summary>Forgets session `session` and the ones after it (a discarded session).</summary>
    public void DropSessionsFrom(int session)
    {
        DropSessions(Manifests, session);
        DropSessions(WrittenBy, session);
    }

    static void DropSessions(List<string> list, int session)
    {
        if (list.Count >= session) list.RemoveRange(session - 1, list.Count - session + 1);
    }
}
