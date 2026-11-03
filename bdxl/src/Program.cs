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
            if (opt.Has("version")) return VersionCommand.Print();
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
        var cat = spec.NeedsCatalogFor(opt) ? OpenCatalog(dir, spec.Name, opt, spec.NeedsFilesFor(opt)) : Catalog.CreateEmpty(dir, spec.Name);

        // the mirror: before anything changes, it has to be there and hold exactly what the catalog holds
        var mirror = cat.Keys != null ? Mirror.Check(dir, cat.Keys) : new Mirror.Report(Mirror.State.None, null, "");
        if (cat.Integrity.State == IntegrityState.Broken && !spec.AllowBrokenCatalog)
        {
            Console.Error.WriteLine($"The catalog in {dir} failed its integrity check, so no work is done:");
            foreach (string problem in cat.Integrity.Problems) Console.Error.WriteLine("  " + problem);
            Console.Error.WriteLine("\nSee 'bdxl catalog check' for the ways out (rollback to an older version, restore a backup, or accept).");
            if (mirror.State is Mirror.State.Differs or Mirror.State.InSync)
                Console.Error.WriteLine($"The catalog mirror {mirror.Path} passes its check: 'bdxl catalog mirror restore' takes the catalog from it.");
            return 4;
        }
        if (!mirror.Ok && cat.Integrity.State != IntegrityState.Broken && !spec.FixesMirror(opt))
        {
            if (!spec.ReadOnlyFor(opt))
            {
                Console.Error.WriteLine(Mirror.Advice(mirror));
                return 4;
            }
            Console.Error.WriteLine($"Warning: {mirror.Message}. Read-only commands still run; nothing can be changed until it is fixed ('catalog mirror status').");
            cat.SaveBlockedBy = Mirror.Advice(mirror);
        }

        Interrupt.Begin(spec);
        try
        {
            return spec.Run(new Ctx(cat, opt));
        }
        finally
        {
            Interrupt.End();
            cat.Keys?.Dispose();
        }
    }

    /// <summary>Unlocks and loads the catalog in dir; a plaintext catalog is encrypted first (after confirmation).</summary>
    static Catalog OpenCatalog(string dir, string command, Options opt, bool files)
    {
        CatalogSetup.RecoverInterruptedMigration(dir);
        KeyRing key;
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
        return Catalog.Load(dir, command, key, files: files);
    }
}
