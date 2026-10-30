namespace Bdxl;

/// <summary>Everything a command needs: the loaded catalog, the options, and shared catalog queries.</summary>
public sealed class Ctx
{
    public Ctx(Catalog cat, Options opt)
    {
        Cat = cat;
        Opt = opt;
        Arc = new Archive(cat);
    }

    public Catalog Cat { get; }
    public Options Opt { get; }
    public Archive Arc { get; }
    public Settings Settings => Cat.Settings;
    /// <summary>The catalog's keys (every command that loads the catalog has them).</summary>
    public KeyRing Keys => Cat.Keys ?? throw new InvalidOperationException("This command runs without the catalog's key.");
    /// <summary>The current master key: what everything new is encrypted with.</summary>
    public MasterKey Key => Keys.Current;
    public List<string> Args => Opt.Args;

    /// <summary>Positional argument `index`, or a usage error.</summary>
    public string Arg(int index, string usage)
    {
        if (Args.Count <= index) throw new UserException("Usage: " + usage);
        return Args[index];
    }
}
