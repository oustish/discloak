namespace Bdxl;

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

    public string? KeyId { get; set; }             // encrypted discs: id of the master key their files are encrypted with

    public int Sessions { get; set; } = 1;         // sessions burned (or staged) onto the disc: 'stage --append' adds one
    public long OccupiedBytes { get; set; }        // space all sessions take: files + per session max(reserve, metadata)
    public AppendSession? PendingAppend { get; set; }   // a session staged with 'stage --append', not burned yet
}
