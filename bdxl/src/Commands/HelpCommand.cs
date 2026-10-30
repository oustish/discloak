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
        Console.WriteLine("bdxl - plan, stage and keep track of encrypted backups on BDXL discs (one encrypted file per file, no archive; compression optional)\n");
        Console.WriteLine("Usage:  bdxl <command> [arguments] [options]      bdxl <command> --help for details\n");
        foreach (var group in CommandTable.All.Where(s => !s.Hidden).GroupBy(s => s.Group))
        {
            Console.WriteLine(group.Key + ":");
            foreach (var spec in group) Console.WriteLine($"  {spec.Name,-12} {spec.Summary}");
            Console.WriteLine();
        }
        Console.WriteLine("Option for every command:  --catalog <dir>   catalog folder (default ./catalog)");
        Console.WriteLine("New here? Run:  bdxl workflow");
    }

    public static void PrintCommand(ICommand spec)
    {
        Console.WriteLine($"bdxl {spec.Name} - {spec.Summary}\n");
        // "a | b": b gets the command name in front unless it already starts with it ("init ... | init --key-from ...")
        var forms = spec.Usage.Split(" | ").Select((form, i) =>
            i == 0 || form == spec.Name || form.StartsWith(spec.Name + " ", StringComparison.Ordinal) ? form : spec.Name + " " + form);
        Console.WriteLine("Usage:  bdxl " + string.Join("\n        bdxl ", forms) + "\n");
        if (spec.Details.Length > 0) Console.WriteLine(spec.Details.TrimEnd());
        Console.WriteLine("\nGeneral option:  --catalog <dir>   catalog folder (default ./catalog)");
    }
}
