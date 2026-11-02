namespace Bdxl;

/// <summary>
/// A session staged onto a burned disc with 'stage --append' that is not burned yet. Remembers what the disc held
/// before it, so 'discard' can take the session back; its copies are the disc's copies without a BackedUpUtc.
/// </summary>
public class AppendSession
{
    public int Number { get; set; }                // 2 = the first appended session
    public DateTime CreatedUtc { get; set; }
    public int FileCountBefore { get; set; }
    public long UsedBytesBefore { get; set; }
    public long OccupiedBytesBefore { get; set; }
    public int PacksBefore { get; set; }
}
