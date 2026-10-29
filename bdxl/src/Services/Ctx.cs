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
    public List<string> Args => Opt.Args;
}
