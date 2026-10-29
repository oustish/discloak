namespace Bdxl;

public sealed class DiscsCommand : ICommand
{
    public string Name => "discs";
    public string Group => CommandGroup.LookingUp;
    public string Summary => "List all discs and their state";
    public string Usage => "discs";
    public string Details => "State is staged, burned, lost or destroyed. Shows encryption, cost and notes.";

    public int Run(Ctx c)
    {
        if (c.Cat.Discs.Count == 0) { Console.WriteLine("No discs yet."); return 0; }
        string currency = c.Settings.Currency;
        foreach (var d in c.Cat.Discs.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{d.Label}  {d.State,-9}  {d.FileCount,9:N0} files  {d.UsedBytes,18:N0} bytes  created {d.CreatedUtc:yyyy-MM-dd}" +
                              (d.BurnedUtc is { } burnedAt ? $"  burned {burnedAt:yyyy-MM-dd}" : "") +
                              (d.LostUtc is { } lostAt ? $"  {d.State} {lostAt:yyyy-MM-dd}" : "") +
                              (d.RestagedUtc is { } restagedAt ? $"  restaged {restagedAt:yyyy-MM-dd}" : "") +
                              (d.Encrypted ? "  encrypted" : "") +
                              (d.Cost is { } cost ? $"  cost {SizeText.Money(cost, currency)}" : "") +
                              (string.IsNullOrEmpty(d.Note) ? "" : $"  # {d.Note}"));
        return 0;
    }
}
