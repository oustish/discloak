using System.Runtime.InteropServices;

namespace Discloak;

/// <summary>
/// How pending files are laid onto blank discs: "largest" (the default: largest file first, each into the disc it
/// fills best - the fullest discs) or "sequential" (in the order of the source folders, as Explorer sorts them, disc
/// after disc - a folder's files stay together, except where a disc ends). The setting 'packing' chooses; plan and
/// stage take --packing for one run.
/// </summary>
public static class Packing
{
    public const string Largest = "largest", Sequential = "sequential";

    public static string Parse(string value) => value.ToLowerInvariant() switch
    {
        Largest => Largest,
        Sequential => Sequential,
        _ => throw new UserException($"packing must be '{Largest}' (largest file first, the fullest discs) or '{Sequential}' (in the order of the source folders).")
    };

    /// <summary>The packing of this run: --packing, else the setting.</summary>
    public static string For(Options opt, Settings settings) => opt.Get("packing") is { } value ? Parse(value) : Parse(settings.Packing);

    public static string Describe(string packing) => packing == Sequential
        ? "sequential (in the order of the source folders, as Explorer sorts them; a folder's files stay together)"
        : "largest (largest file first, each into the disc it fills best)";
}

/// <summary>
/// File paths in the order Windows Explorer shows them: folder by folder (so a folder's files stay together), in each
/// folder its subfolders first, then its files, each name compared as Explorer compares names - letters without
/// case, numbers by value ("Track 2" before "Track 10").
/// </summary>
public sealed class ExplorerOrder : IComparer<string>
{
    public static readonly ExplorerOrder Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Two names as Explorer orders them (names Explorer sees as equal, e.g. "a01" and "a1": by their characters).</summary>
    public static int CompareNames(string a, string b)
    {
        int n = OperatingSystem.IsWindows() ? StrCmpLogicalW(a, b) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return n != 0 ? n : string.CompareOrdinal(a, b);
    }

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        var x = a.Split('/', '\\');
        var y = b.Split('/', '\\');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            // in a folder, its subfolders come before its files, as Explorer lists them
            bool xFolder = i < x.Length - 1, yFolder = i < y.Length - 1;
            if (xFolder != yFolder) return xFolder ? -1 : 1;
            int n = CompareNames(x[i], y[i]);
            if (n != 0) return n;
        }
        return x.Length.CompareTo(y.Length);
    }
}
