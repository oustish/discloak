namespace Discloak;

public sealed class PlanCommand : ICommand
{
    public string Name => "plan";
    public string Group => CommandGroup.Indexing;
    public string Summary => "Show which files would go on which blank disc (changes nothing)";
    public string Usage => "plan [--split] [--without-program] [--append <label> [--mount <path>]]";
    public bool ReadOnlyFor(Options opt) => true;
    public string Details => """
        Allocates all pending files to the blank discs registered with 'blanks'. Prints how full each disc would be
        and, if the data does not fit on the designated discs, which files are left over and roughly how many
        more discs are needed. Exit code 2 = not everything fits.
        Sizes are those 'stage' writes (compressed where set); a disc's room is what its packs can hold.
          --split     cut files larger than every blank disc into parts (see 'stage --help')
          --append L  instead: what 'stage --append L' would put into a new session of the burned disc L
          --without-program   as 'stage --without-program': no room kept for the program folder
        Pending = on the source, not ignored, and no live disc holds identical content (hash-based dedup).
        """;

    /// <summary>
    /// Allocates pending files to the registered blank discs (see 'blanks') and reports whether everything fits.
    /// Changes nothing; 'stage' uses exactly the same allocation.
    /// </summary>
    public int Run(Ctx c)
    {
        if (c.Opt.Get("append") is { } appendTo) return PlanAppend(c, appendTo);
        var plan = c.Arc.MakePlan(c.Opt.Has("split"), programSpace: DiscProgram.SpaceFor(c.Opt));
        Console.WriteLine($"Pending: {plan.PendingFiles:N0} file(s), {SizeText.Bytes(plan.PendingBytes)} bytes{plan.CopiesNote}");
        int splitFiles = plan.Pending.Where(p => p.IsPart).Select(p => p.File).Distinct().Count();
        if (splitFiles > 0)
            Console.WriteLine($"({splitFiles:N0} file(s) are split across discs: {plan.Pending.Count(p => p.IsPart):N0} part(s) still to store.)");

        int ignored = c.Cat.Files.Count(f => f.Ignored);
        if (ignored > 0) Console.WriteLine($"({ignored:N0} catalogued file(s) are ignored by rules and not part of the plan.)");

        if (c.Cat.Blanks.Count == 0)
        {
            if (plan.Pending.Count > 0)
                Console.WriteLine("No blank discs are registered, so nothing can be allocated. Register some, e.g.:\n" +
                                  "  blanks add 5 --size 96882130944 --reserve 200MB");
            return plan.Pending.Count > 0 ? 2 : 0;
        }

        long program = DiscProgram.SpaceFor(c.Opt);
        Console.WriteLine($"Blank discs: {c.Cat.Blanks.Count} ({SizeText.Bytes(c.Cat.Blanks.Sum(b => b.Usable))} usable bytes)" +
                          (program > 0 ? $"; {SizeText.Bytes(program)} bytes of each are kept for the program folder (--without-program: none)" : "") + "\n");
        foreach (var bin in plan.Bins.Where(b => b.Pieces.Count > 0))
        {
            int parts = bin.Pieces.Count(p => p.IsPart);
            Console.WriteLine($"  {bin.Blank.Id}  {bin.Blank.Capacity,18:N0} B disc: {bin.Pieces.Count,8:N0} files, {bin.Used,18:N0} bytes " +
                              $"({100.0 * bin.Used / bin.Usable:F1}% of usable)" + (parts > 0 ? $"  incl. {parts} part(s) of split files" : ""));
        }
        int unused = plan.Bins.Count(b => b.Pieces.Count == 0);
        if (unused > 0) Console.WriteLine($"  {unused} blank disc(s) would stay unused.");

        if (plan.Unplaced.Count == 0)
        {
            Console.WriteLine("\nAll pending data fits on the designated blank discs.");
            return 0;
        }

        long left = plan.Unplaced.Sum(p => p.Length);
        Console.WriteLine($"\nNOT ALL DATA FITS: {plan.Unplaced.Count:N0} file(s), {SizeText.Bytes(left)} bytes cannot be placed on the {c.Cat.Blanks.Count} designated blank disc(s).");
        Archive.PrintTooBig(plan.TooBig);
        var rest = plan.Unplaced.Where(p => !plan.TooBig.Contains(p.File)).ToList();
        if (rest.Count > 0)
        {
            // more discs only help with what fits on one disc; files larger than any disc need --split (or bigger discs)
            long restBytes = rest.Sum(p => p.Length), biggest = c.Cat.Blanks.Max(b => b.Usable);
            Console.WriteLine($"Roughly {(restBytes + biggest - 1) / biggest} more disc(s) of the largest size would be needed for the rest.");
            Console.WriteLine("Left over (do not fit in the remaining space):");
            foreach (var p in rest.OrderByDescending(x => x.Length).Take(20))
                Console.WriteLine($"  {p.Name}  ({SizeText.Bytes(p.Length)} bytes)");
            if (rest.Count > 20) Console.WriteLine($"  ... and {rest.Count - 20:N0} more");
        }
        return 2;
    }

    /// <summary>plan --append: what one more session of a burned disc would hold (stored sizes).</summary>
    static int PlanAppend(Ctx c, string label)
    {
        var target = AppendTarget.Open(c, label);
        var plan = c.Arc.MakePlan(c.Opt.Has("split"), [target.AsBlank()], DiscProgram.SpaceFor(c.Opt));
        var bin = plan.Bins[0];
        Console.WriteLine($"Pending: {plan.PendingFiles:N0} file(s), {SizeText.Bytes(plan.PendingBytes)} bytes{plan.CopiesNote}");
        Console.WriteLine($"{target.Disc.Label}: {SizeText.Bytes(target.Free)} bytes free for files in session {target.Session} " +
                          $"(reserve of {SizeText.Bytes(target.Reserve)} bytes kept for its metadata)");
        Console.WriteLine($"  would hold {bin.Pieces.Count:N0} file(s), {SizeText.Bytes(bin.Used)} bytes");
        if (plan.Unplaced.Count == 0) return 0;
        Console.WriteLine($"  {plan.Unplaced.Count:N0} file(s), {SizeText.Bytes(plan.Unplaced.Sum(p => p.Length))} bytes would stay pending (see 'plan').");
        return 2;
    }
}
