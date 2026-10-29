namespace Bdxl;

public static class Program
{
    public static int Main(string[] argv)
    {
        // default Windows console code page mangles non-ASCII file names (ą, ł, ...)
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            return Run(argv);
        }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or JsonException or CryptographicException or InvalidDataException
                                       or FormatException or ArgumentException or OverflowException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    static int Run(string[] argv)
    {
        var opt = Options.Parse(argv);

        if (opt.Command == null)
        {
            HelpCommand.PrintOverview();
            return opt.Has("help") ? 0 : 1;
        }

        var spec = CommandTable.Find(opt.Command);
        if (spec == null)
        {
            Console.Error.WriteLine($"Unknown command '{opt.Command}'. Run 'bdxl help' for the list of commands.");
            return 1;
        }
        if (opt.Has("help") && spec.Name != "help")
        {
            HelpCommand.PrintCommand(spec);
            return 0;
        }

        string dir = Path.GetFullPath(opt.GetOr("catalog", "catalog"));
        var cat = spec.NeedsCatalog ? Catalog.Load(dir, spec.Name) : Catalog.CreateEmpty(dir, spec.Name);

        if (cat.Integrity.State == IntegrityState.Broken && !spec.AllowBrokenCatalog)
        {
            Console.Error.WriteLine($"The catalog in {dir} failed its integrity check, so no work is done:");
            foreach (string problem in cat.Integrity.Problems) Console.Error.WriteLine("  " + problem);
            Console.Error.WriteLine("\nSee 'bdxl catalog check' for the ways out (rollback to an older version, restore a backup, or accept hand edits).");
            return 4;
        }
        if (cat.Integrity.State == IntegrityState.Legacy)
            Console.Error.WriteLine("Note: this catalog has no integrity hashes yet; they are written the next time it changes.");

        Interrupt.Begin(spec);
        try
        {
            return spec.Run(new Ctx(cat, opt));
        }
        finally
        {
            Interrupt.End();
        }
    }
}
