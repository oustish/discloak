namespace Bdxl;

/// <summary>One bdxl command: its help text and what it does. Every command is listed in <see cref="CommandTable"/>.</summary>
public interface ICommand
{
    string Name { get; }
    string Group { get; }      // heading it is listed under in 'bdxl help' (see CommandGroup)
    string Summary { get; }
    string Usage { get; }
    string Details { get; }
    bool AllowBrokenCatalog => false;   // may run when the catalog failed its integrity check
    bool NeedsCatalog => true;          // false: does not read the catalog at all (or opens it itself)
    bool NeedsCatalogFor(Options opt) => NeedsCatalog;   // the same, decided per run (e.g. by sub-command)
    bool Hidden => false;               // not listed by 'bdxl help' (internal)
    bool Interruptible => false;        // stops cleanly on Ctrl+C by checking Interrupt.Requested (others are stopped at once)

    int Run(Ctx c);
}

/// <summary>Headings of 'bdxl help'.</summary>
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
