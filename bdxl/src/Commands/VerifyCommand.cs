namespace Bdxl;

public sealed class VerifyCommand : ICommand
{
    public string Name => "verify";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Re-read a disc and compare it with the catalog";
    public string Usage => "verify <label> <mountedDiscPath> [--deep]";
    public bool Interruptible => true;
    public string Details => """
        Reads every pack of the disc and compares its SHA-256 with the catalog - nothing is decrypted, so no password
        is needed for that part. --deep also decrypts every pack (each 1 MiB chunk is authenticated) and checks every
        file on the disc against its hash, in the same single read of the disc.
        A damaged pack is named with the files that have data in it; those files can be restored from another copy,
        or the disc rebuilt with 'restage' (which reuses the intact packs).
        Copies recorded by 'scandisc' are marked 'unverified' until this has checked their packs.
        Ctrl+C stops after the pack being read and reports what was checked so far.
        """;

    public int Run(Ctx c)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, Usage));
        string mount = Path.GetFullPath(c.Arg(1, Usage));
        bool deep = c.Opt.Has("deep");
        if (DiscFiles.ReadInfo(mount) is { } info)
        {
            if (!info.Label.Equals(disc.Label, StringComparison.OrdinalIgnoreCase))
                throw new UserException($"The disc in {mount} is {info.Label}, not {disc.Label}.");
            if (deep && info.Unreadable() is { } why) throw new UserException($"{disc.Label} cannot be decrypted: {why}.");
        }
        var copies = c.Arc.CopiesOn(disc.Label);
        int packs = Math.Max(disc.Packs.Count, copies.Count == 0 ? 0 : DiscFormat.PackOf(copies[^1].Copy.End - 1));
        if (packs == 0) { Console.WriteLine($"{disc.Label} has no packs in the catalog."); return 0; }

        var badPacks = new SortedDictionary<int, string>();   // pack -> what is wrong with it
        int checkedPacks = 0, unknown = 0;
        void Judge(int pack, string? sha, string? problem)
        {
            checkedPacks = Math.Max(checkedPacks, pack);
            string? expected = pack <= disc.Packs.Count ? disc.Packs[pack - 1] : null;
            if (problem != null && sha == null) badPacks[pack] = problem;
            else if (expected == null) unknown++;
            else if (sha != null && !Hashing.SameHash(sha, expected)) badPacks[pack] = $"pack {pack} differs from the catalog";
            else if (problem != null) badPacks[pack] = problem;   // the right bytes, but they do not decrypt (another key?)
        }

        int badFiles = 0;
        var damagedFiles = new List<string>();
        if (!deep)
        {
            if (disc.Packs.Count == 0) throw new UserException($"The catalog has no pack hashes for {disc.Label}; use --deep (decrypts and checks every file).");
            for (int p = 1; p <= disc.Packs.Count && !Interrupt.Requested; p++)
            {
                string path = Path.Combine(mount, DiscFormat.PackPath(p));
                if (!File.Exists(path)) { Judge(p, null, $"pack {p} is missing"); continue; }
                try { Judge(p, Hashing.HashFile(path, Interrupt.Token), null); }
                catch (OperationCanceledException) { break; }
                catch (IOException ex) { Judge(p, null, $"pack {p} cannot be read ({ex.Message})"); }
            }
        }
        else
        {
            // every pack read once, in order: hashed and decrypted; the files are checked as their packs come by
            using var reader = new PackReader(mount, c.Keys, wholePacks: true) { PackRead = Judge };
            try
            {
                foreach (var (file, copy) in copies)
                {
                    if (Interrupt.Requested) break;
                    try
                    {
                        using var stored = reader.Open(copy.At, copy.StoredSize);
                        string got = StoredForm.Read(stored, copy.Compression, Stream.Null, Interrupt.Token);
                        if (!Hashing.SameHash(got, copy.ContentSha256)) { badFiles++; damagedFiles.Add($"{Describe(file, copy)}  (decrypts, but the content differs from the catalog)"); }
                    }
                    catch (InvalidDataException ex) { badFiles++; damagedFiles.Add($"{Describe(file, copy)}  ({ex.Message})"); }
                }
                if (!Interrupt.Requested) reader.ReadThrough(packs);   // packs that hold no file (padding) are checked too
            }
            catch (OperationCanceledException) { }
        }

        foreach (var (pack, problem) in badPacks) Console.WriteLine($"DAMAGED  {problem}");
        if (!deep)   // name the files that have data in a damaged pack
            foreach (var (file, copy) in copies.Where(x => badPacks.Keys.Any(p => Overlaps(x.Copy, p))))
                damagedFiles.Add(Describe(file, copy));
        if (damagedFiles.Count > 0)
        {
            Console.WriteLine(deep ? "Files that do not come back intact:" : "Files with data in the damaged packs:");
            foreach (string f in damagedFiles.Take(50)) Console.WriteLine("  " + f);
            if (damagedFiles.Count > 50) Console.WriteLine($"  ... and {damagedFiles.Count - 50:N0} more");
        }

        bool complete = !Interrupt.Requested && checkedPacks >= packs;
        int ok = checkedPacks - badPacks.Count - unknown;
        Console.WriteLine($"\n{disc.Label}: {ok:N0} of {packs:N0} pack(s) OK, {badPacks.Count:N0} damaged" +
                          (unknown > 0 ? $", {unknown:N0} without a hash in the catalog" : "") +
                          (deep ? $"; {copies.Count - badFiles:N0} of {copies.Count:N0} file(s) checked intact" : "") + ".");

        // copies taken from the disc's manifest (scandisc) are verified once their packs check out
        bool changed = false;
        if (complete && badPacks.Count == 0 && (deep ? badFiles == 0 : unknown == 0))
            foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label && b.Unverified))
            {
                b.Unverified = false;
                changed = true;
            }
        if (changed) c.Cat.Save();   // a plain re-check changes nothing and should not use up a history slot
        if (Interrupt.Requested)
        {
            Console.WriteLine($"Stopped by Ctrl+C: {packs - checkedPacks:N0} of {packs:N0} pack(s) were not checked.");
            return Interrupt.ExitCode;
        }
        return badPacks.Count + badFiles == 0 ? 0 : 3;
    }

    static bool Overlaps(BackupRef copy, int pack) =>
        copy.StoredSize > 0 && DiscFormat.PackOf(copy.At) <= pack && DiscFormat.PackOf(copy.End - 1) >= pack;

    static string Describe(FileRecord f, BackupRef copy) =>
        $"{f.RootName}/{f.RelPath}" + (copy.IsPart ? $" (part {copy.Part}/{copy.Parts})" : "") +
        (Hashing.SameHash(copy.Sha256, f.Sha256) ? "" : " (an older version)");
}
