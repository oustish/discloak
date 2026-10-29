namespace Bdxl;

public sealed class HelpCommand : ICommand
{
    public string Name => "help";
    public string Group => CommandGroup.Help;
    public string Summary => "Show this list, or help for one command";
    public string Usage => "help [<command>]    (or: <command> --help)";
    public string Details => "";
    public bool AllowBrokenCatalog => true;
    public bool NeedsCatalog => false;

    public int Run(Ctx c)
    {
        if (c.Args.Count == 0) { PrintOverview(); return 0; }
        var spec = CommandTable.Find(c.Args[0]);
        if (spec == null)
        {
            Console.Error.WriteLine($"No command called '{c.Args[0]}'.");
            PrintOverview();
            return 1;
        }
        PrintCommand(spec);
        return 0;
    }

    public static void PrintOverview()
    {
        Console.WriteLine("bdxl - plan, stage and keep track of backups on BDXL discs (files are copied as they are: no archive, no compression)\n");
        Console.WriteLine("Usage:  bdxl <command> [arguments] [options]      bdxl <command> --help for details\n");
        foreach (var group in CommandTable.All.GroupBy(s => s.Group))
        {
            Console.WriteLine(group.Key + ":");
            foreach (var spec in group) Console.WriteLine($"  {spec.Name,-10} {spec.Summary}");
            Console.WriteLine();
        }
        Console.WriteLine("Option for every command:  --catalog <dir>   catalog folder (default ./catalog)");
        Console.WriteLine("New here? Run:  bdxl workflow");
    }

    public static void PrintCommand(ICommand spec)
    {
        Console.WriteLine($"bdxl {spec.Name} - {spec.Summary}\n");
        Console.WriteLine("Usage:  bdxl " + spec.Usage.Replace(" | ", "\n        bdxl " + spec.Name + " ") + "\n");
        if (spec.Details.Length > 0) Console.WriteLine(spec.Details.TrimEnd());
        Console.WriteLine("\nGeneral option:  --catalog <dir>   catalog folder (default ./catalog)");
    }
}
