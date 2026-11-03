namespace Discloak;

/// <summary>
/// blanks [list]
/// blanks add &lt;count&gt; [--size 96882130944|100GB|90GiB] [--reserve 200MB] [--cost 24.99]
/// blanks reserve &lt;size&gt; [--size S]      change the reserve of the known blank discs
/// blanks cost &lt;amount&gt; [--size S]        change the price of the known blank discs
/// blanks remove &lt;id|all&gt; [--size S]
/// </summary>
public sealed class BlanksCommand : ICommand
{
    public string Name => "blanks";
    public string Group => CommandGroup.Indexing;
    public string Summary => "Register the blank discs you own (sizes, reserve, price)";
    public string Usage => "blanks [list] | add <count> [--size S] [--reserve R] [--cost P] | reserve <R> [--size S] | cost <P> [--size S] | remove <id|all> [--size S]";
    public bool NeedsFilesFor(Options opt) => false;   // blank discs only
    public bool ReadOnlyFor(Options opt) => SubCommand.Is(opt, "list", "list");
    public string Details => """
          blanks add 5 --size 96882130944 --reserve 200MB --cost 45    five discs; sizes accept 25GB, 90GiB, bytes ...
          blanks reserve 300MB            change the reserve of ALL blank discs   (--size S: only discs of that size)
          blanks cost 39.99               change the price of all blank discs
          blanks remove B0003 | all       forget a blank disc
        The reserve is space deliberately left free (filesystem overhead, a catalog backup with --with-catalog).
        A blank disc is used up when a disc is staged from it; 'discard' gives it back.
        """;

    const string DefaultSize = "96882130944";

    public int Run(Ctx c)
    {
        var a = c.Args;
        string sub = a.Count > 0 ? a[0].ToLowerInvariant() : "list";
        long? sizeFilter = sub != "add" && c.Opt.Get("size") is { } sizeText ? SizeText.Parse(sizeText) : null;

        if (sub == "list" && a.Count <= 1) return List(c);

        if (sub == "add" && a.Count == 2)
        {
            if (!int.TryParse(a[1], out int count) || count < 1)
                throw new UserException("blanks add <count>: the count must be a positive number.");
            long cap = SizeText.Parse(c.Opt.GetOr("size", DefaultSize));
            long res = SizeText.Parse(c.Opt.GetOr("reserve", "0"));
            decimal? cost = c.Opt.GetMoney("cost");
            if (cap <= 0 || res < 0 || res >= cap) throw new UserException("The reserve must be smaller than the disc size.");
            for (int n = 0; n < count; n++)
                c.Cat.Blanks.Add(new BlankDisc { Id = c.Arc.NextBlankId(), Capacity = cap, Reserve = res, Cost = cost, AddedUtc = DateTime.UtcNow });
            c.Cat.Save();
            Console.WriteLine($"Added {count} blank disc(s) of {SizeText.Bytes(cap)} bytes, reserve {SizeText.Bytes(res)}" +
                              (cost is { } cc ? $", {SizeText.Money(cc, c.Settings.Currency)} each" : "") + ".\n");
            return List(c);
        }

        if (sub == "reserve" && a.Count == 2)
        {
            long res = SizeText.Parse(a[1]);
            var targets = c.Cat.Blanks.Where(b => sizeFilter == null || b.Capacity == sizeFilter).ToList();
            if (targets.Count == 0) { Console.WriteLine("No matching blank discs."); return 1; }
            var tooSmall = targets.FirstOrDefault(b => res >= b.Capacity);
            if (tooSmall != null)
                throw new UserException($"A reserve of {SizeText.Bytes(res)} bytes is not smaller than {tooSmall.Id} ({SizeText.Bytes(tooSmall.Capacity)} bytes).");
            foreach (var blank in targets) blank.Reserve = res;
            c.Cat.Save();
            Console.WriteLine($"Reserve set to {SizeText.Bytes(res)} bytes on {targets.Count} blank disc(s). (Discs that are already staged keep theirs.)\n");
            return List(c);
        }

        if (sub == "cost" && a.Count == 2)
        {
            decimal price = SizeText.ParseMoney(a[1]);
            var targets = c.Cat.Blanks.Where(b => sizeFilter == null || b.Capacity == sizeFilter).ToList();
            if (targets.Count == 0) { Console.WriteLine("No matching blank discs."); return 1; }
            foreach (var blank in targets) blank.Cost = price;
            c.Cat.Save();
            Console.WriteLine($"Price set to {SizeText.Money(price, c.Settings.Currency)} on {targets.Count} blank disc(s).\n");
            return List(c);
        }

        if (sub == "remove" && a.Count == 2)
        {
            int removed = a[1].Equals("all", StringComparison.OrdinalIgnoreCase)
                ? c.Cat.Blanks.RemoveAll(b => sizeFilter == null || b.Capacity == sizeFilter)
                : c.Cat.Blanks.RemoveAll(b => b.Id.Equals(a[1], StringComparison.OrdinalIgnoreCase));
            if (removed == 0) { Console.WriteLine("Nothing removed."); return 1; }
            c.Cat.Save();
            Console.WriteLine($"Removed {removed} blank disc(s).");
            return 0;
        }

        throw new UserException("Usage: blanks [list] | add <count> [--size S] [--reserve R] [--cost P] | reserve <R> [--size S] | cost <P> [--size S] | remove <id|all> [--size S]");
    }

    static int List(Ctx c)
    {
        if (c.Cat.Blanks.Count == 0)
        {
            Console.WriteLine("No blank discs registered. Add some:  blanks add <count> --size <bytes|90GiB|100GB> [--reserve <size>] [--cost <price>]");
            return 0;
        }
        foreach (var g in c.Cat.Blanks.GroupBy(b => (b.Capacity, b.Reserve, b.Cost))
                     .OrderByDescending(g => g.Key.Capacity).ThenBy(g => g.Key.Reserve))
        {
            var ids = g.Select(b => b.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            string idText = ids.Count <= 8 ? string.Join(", ", ids) : $"{ids[0]} .. {ids[^1]}";
            string price = g.Key.Cost is { } p ? $", {SizeText.Money(p, c.Settings.Currency)} each" : "";
            Console.WriteLine($"{g.Count(),4} x {g.Key.Capacity,18:N0} bytes, reserve {g.Key.Reserve:N0} (usable {g.Key.Capacity - g.Key.Reserve:N0}){price}   {idText}");
        }
        Console.WriteLine($"Total: {c.Cat.Blanks.Count} disc(s), {SizeText.Bytes(c.Cat.Blanks.Sum(b => b.Usable))} usable bytes.");
        return 0;
    }
}
