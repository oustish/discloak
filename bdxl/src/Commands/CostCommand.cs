namespace Bdxl;

/// <summary>
/// cost report                   what the discs cost: per disc, per GB and per TB
/// cost set &lt;label&gt; &lt;amount&gt;   record what a disc cost (also for imported / pre-existing discs)
/// (blank discs get their price with 'blanks add --cost' / 'blanks cost'; it is copied to the disc when it is staged)
/// </summary>
public sealed class CostCommand : ICommand
{
    public string Name => "cost";
    public string Group => CommandGroup.LookingUp;
    public string Summary => "What the discs cost: per disc, per GB and per TB";
    public string Usage => "cost [report] | cost set <label> <amount>";
    public bool NeedsFilesFor(Options opt) => false;   // discs and blank discs only
    public bool ReadOnlyFor(Options opt) => SubCommand.Is(opt, "report", "report");
    public string Details => """
        Prices come from 'blanks add --cost', 'blanks cost', 'scandisc --cost' and 'cost set <label> <amount>' (also for
        discs you bought earlier). 'cost report' shows cost per disc, per GB and per TB of data stored and of
        capacity, and the value of the blank discs in stock. GB = 10^9 bytes, TB = 10^12 bytes.
        """;

    const decimal GB = 1_000_000_000m;
    const decimal TB = 1_000_000_000_000m;

    public int Run(Ctx c)
    {
        string sub = c.Args.Count > 0 ? c.Args[0].ToLowerInvariant() : "report";
        if (sub == "set")
        {
            if (c.Args.Count != 3) throw new UserException("Usage: cost set <label> <amount>");
            var disc = c.Arc.FindDisc(c.Args[1]);
            disc.Cost = SizeText.ParseMoney(c.Args[2]);
            c.Cat.Save();
            Console.WriteLine($"{disc.Label}: cost {SizeText.Money(disc.Cost.Value, c.Settings.Currency)}.");
            return 0;
        }
        if (sub == "report" && c.Args.Count <= 1) return Report(c);
        throw new UserException("Usage: cost report | cost set <label> <amount>");
    }

    static string PerUnit(decimal cost, decimal bytes, decimal unit, string currency)
    {
        if (bytes <= 0) return "n/a";
        return SizeText.Money(cost / (bytes / unit), currency);
    }

    static int Report(Ctx c)
    {
        string cur = c.Settings.Currency;
        var priced = c.Cat.Discs.Where(d => d.Cost != null && d.State != "destroyed").OrderBy(d => d.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var unpriced = c.Cat.Discs.Where(d => d.Cost == null).ToList();

        if (priced.Count == 0 && c.Cat.Blanks.All(b => b.Cost == null))
        {
            Console.WriteLine("No prices recorded yet. Use:  blanks add <n> --cost <price>   |   cost set <label> <price>   |   scandisc <path> --cost <price>");
            return 0;
        }

        Console.WriteLine($"{"Disc",-12} {"State",-9} {"Data (bytes)",18} {"Cost",14} {"per GB",12} {"per TB",12}");
        foreach (var d in priced)
            Console.WriteLine($"{d.Label,-12} {d.State,-9} {d.UsedBytes,18:N0} {SizeText.Money(d.Cost!.Value, cur),14} " +
                              $"{PerUnit(d.Cost.Value, d.UsedBytes, GB, cur),12} {PerUnit(d.Cost.Value, d.UsedBytes, TB, cur),12}");

        if (priced.Count > 0)
        {
            decimal total = priced.Sum(d => d.Cost!.Value);
            decimal data = priced.Sum(d => (decimal)d.UsedBytes);
            var withCapacity = priced.Where(d => d.Capacity != null).ToList();
            decimal capCost = withCapacity.Sum(d => d.Cost!.Value);
            decimal capBytes = withCapacity.Sum(d => (decimal)d.Capacity!.Value);

            Console.WriteLine($"\nDiscs with a price: {priced.Count}, total {SizeText.Money(total, cur)}");
            Console.WriteLine($"  per GB / TB of data stored on them : {PerUnit(total, data, GB, cur)} / {PerUnit(total, data, TB, cur)}   ({SizeText.Bytes((long)data)} bytes)");
            if (withCapacity.Count > 0)
                Console.WriteLine($"  per GB / TB of disc capacity       : {PerUnit(capCost, capBytes, GB, cur)} / {PerUnit(capCost, capBytes, TB, cur)}   ({withCapacity.Count} disc(s) with known capacity)");

            var live = priced.Where(d => d.State is "burned" or "staged").ToList();
            var lostCost = priced.Where(d => d.State == "lost").Sum(d => d.Cost!.Value);
            if (lostCost > 0) Console.WriteLine($"  of which spent on discs now marked lost: {SizeText.Money(lostCost, cur)}");
            if (live.Count != priced.Count)
            {
                decimal liveCost = live.Sum(d => d.Cost!.Value);
                decimal liveData = live.Sum(d => (decimal)d.UsedBytes);
                Console.WriteLine($"  only discs that still exist ({live.Count}): {SizeText.Money(liveCost, cur)}, per GB / TB {PerUnit(liveCost, liveData, GB, cur)} / {PerUnit(liveCost, liveData, TB, cur)}");
            }
        }

        var pricedBlanks = c.Cat.Blanks.Where(b => b.Cost != null).ToList();
        if (pricedBlanks.Count > 0)
        {
            decimal value = pricedBlanks.Sum(b => b.Cost!.Value);
            decimal usable = pricedBlanks.Sum(b => (decimal)b.Usable);
            Console.WriteLine($"\nBlank discs in stock with a price: {pricedBlanks.Count}, worth {SizeText.Money(value, cur)}; " +
                              $"per GB / TB of usable space {PerUnit(value, usable, GB, cur)} / {PerUnit(value, usable, TB, cur)}");
        }
        if (unpriced.Count > 0)
            Console.WriteLine($"\n{unpriced.Count} disc(s) have no price (left out): {string.Join(", ", unpriced.Take(10).Select(d => d.Label))}{(unpriced.Count > 10 ? ", ..." : "")}");
        Console.WriteLine("GB = 10^9 bytes, TB = 10^12 bytes.");
        return 0;
    }
}
