namespace Bdxl;

public sealed class DiscsCommand : ICommand
{
    public string Name => "discs";
    public string Group => CommandGroup.LookingUp;
    public string Summary => "List all discs and their state";
    public string Usage => "discs";
    public bool NeedsFilesFor(Options opt) => false;   // discs only
    public bool ReadOnlyFor(Options opt) => true;
    public string Details => """
        State is staged, burned, lost or destroyed. Shows its packs, cost, the bdxl build that staged it (its latest
        session; 'version' explains the form) and notes, the sessions of a disc that
        'stage --append' added to, and how much a burned disc of known size still has free for files (as far as the
        catalog knows - a finalized disc takes nothing more).
        """;

    public int Run(Ctx c)
    {
        if (c.Cat.Discs.Count == 0) { Console.WriteLine("No discs yet."); return 0; }
        string currency = c.Settings.Currency;
        foreach (var d in c.Cat.Discs.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{d.Label}  {d.State,-9}  {d.FileCount,9:N0} files  {d.UsedBytes,18:N0} bytes  created {d.CreatedUtc:yyyy-MM-dd}" +
                              (d.BurnedUtc is { } burnedAt ? $"  burned {burnedAt:yyyy-MM-dd}" : "") +
                              (d.LostUtc is { } lostAt ? $"  {d.State} {lostAt:yyyy-MM-dd}" : "") +
                              (d.RestagedUtc is { } restagedAt ? $"  restaged {restagedAt:yyyy-MM-dd}" : "") +
                              (d.Packs.Count > 0 ? $"  {d.Packs.Count:N0} packs" : "") +
                              (d.Sessions > 1 ? $"  {d.Sessions} sessions" : "") +
                              (d.PendingAppend is { } session ? $" (session {session.Number} staged, not burned)" : "") +
                              (d.State == "burned" && AppendTarget.FreeFor(d) is { } free ? $"  {SizeText.Bytes(free)} bytes free" : "") +
                              (d.Cost is { } cost ? $"  cost {SizeText.Money(cost, currency)}" : "") +
                              (d.WrittenBy.LastOrDefault(w => w.Length > 0) is { } by ? $"  by {by}" : "") +
                              (string.IsNullOrEmpty(d.Note) ? "" : $"  # {d.Note}"));
        return 0;
    }
}
