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
        catch (OperationCanceledException)   // an interruptible command that did not handle its own stop
        {
            Console.Error.WriteLine("\nINTERRUPTED: stopped by Ctrl+C before it finished. The catalog is as it was after its last save.");
            return Interrupt.ExitCode;
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
        var cat = spec.NeedsCatalogFor(opt) ? OpenCatalog(dir, spec.Name, opt) : Catalog.CreateEmpty(dir, spec.Name);

        if (cat.Integrity.State == IntegrityState.Broken && !spec.AllowBrokenCatalog)
        {
            Console.Error.WriteLine($"The catalog in {dir} failed its integrity check, so no work is done:");
            foreach (string problem in cat.Integrity.Problems) Console.Error.WriteLine("  " + problem);
            Console.Error.WriteLine("\nSee 'bdxl catalog check' for the ways out (rollback to an older version, restore a backup, or accept).");
            return 4;
        }

        Interrupt.Begin(spec);
        try
        {
            return spec.Run(new Ctx(cat, opt));
        }
        finally
        {
            Interrupt.End();
            cat.Key?.Dispose();
        }
    }

    /// <summary>Unlocks and loads the catalog in dir; a plaintext catalog is encrypted first (after confirmation).</summary>
    static Catalog OpenCatalog(string dir, string command, Options opt)
    {
        CatalogSetup.RecoverInterruptedMigration(dir);
        MasterKey key;
        switch (Catalog.Detect(dir))
        {
            case CatalogKind.None:
                throw new UserException($"No catalog in {dir}. Create one with:  bdxl init" + (opt.Has("catalog") ? $" --catalog \"{dir}\"" : ""));
            case CatalogKind.Plain:
                Console.Error.WriteLine($"The catalog in {dir} is not encrypted. bdxl keeps every catalog encrypted now; this is done once.");
                if (!Ui.Confirm(opt, "Encrypt it now?")) throw new UserException("The catalog was left as it is; nothing was done.");
                key = CatalogSetup.Migrate(dir, opt);
                break;
            default:
                key = CatalogSetup.Unlock(dir, opt);
                if (CatalogSetup.HasPlaintextLeftovers(dir)) CatalogSetup.FinishMigration(dir, key);
                break;
        }
        return Catalog.Load(dir, command, key);
    }
}
