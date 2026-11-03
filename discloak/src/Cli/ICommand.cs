namespace Discloak;

/// <summary>One discloak command: its help text and what it does. Every command is listed in <see cref="CommandTable"/>.</summary>
public interface ICommand
{
    string Name { get; }
    string Group { get; }      // heading it is listed under in 'discloak help' (see CommandGroup)
    string Summary { get; }
    string Usage { get; }
    string Details { get; }
    bool AllowBrokenCatalog => false;   // may run when the catalog failed its integrity check
    bool NeedsCatalog => true;          // false: does not read the catalog at all (or opens it itself)
    bool NeedsCatalogFor(Options opt) => NeedsCatalog;   // the same, decided per run (e.g. by sub-command)
    bool Hidden => false;               // not listed by 'discloak help' (internal)
    bool Interruptible => false;        // stops cleanly on Ctrl+C by checking Interrupt.Requested (others are stopped at once)
    bool ReadOnlyFor(Options opt) => false;      // never changes the catalog in this run: may run while its mirror is not in step
    bool FixesMirror(Options opt) => false;      // deals with the mirror itself (sync, restore, setting it): runs when it is not in step
    bool NeedsFilesFor(Options opt) => true;     // false: never uses the file list - it is not read (much faster for a large catalog)

    int Run(Ctx c);
}

/// <summary>Headings of 'discloak help'.</summary>
public static class CommandGroup
{
    public const string Indexing = "Indexing and planning";
    public const string MakingDiscs = "Making discs";
    public const string ExistingDiscs = "Existing discs";
    public const string LookingUp = "Looking things up";
    public const string Recovery = "Recovery";
    public const string Catalog = "The catalog itself";
    public const string Help = "Help";
}

/// <summary>For ICommand.ReadOnlyFor: the run's sub-command (the first argument, or the default) is one of these.</summary>
public static class SubCommand
{
    public static bool Is(Options opt, string defaultSub, params string[] subs) =>
        subs.Contains(opt.Args.Count > 0 ? opt.Args[0].ToLowerInvariant() : defaultSub, StringComparer.Ordinal);
}
