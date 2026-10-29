namespace Bdxl;

/// <summary>burned, verify, discard, discs, lost/destroyed, found</summary>
public static class DiscCommands
{
    static string Need(Ctx c, int index, string usage)
    {
        if (c.Args.Count <= index) throw new UserException("Usage: " + usage);
        return c.Args[index];
    }

    // ---- burned ---------------------------------------------------------------------------

    public static int Burned(Ctx c)
    {
        var disc = c.Arc.FindDisc(Need(c, 0, "burned <label> [--date yyyy-MM-dd]"));
        if (disc.State is "lost" or "destroyed")
            throw new UserException($"{disc.Label} is {disc.State}. Use 'restage' to rebuild it first.");
        if (disc.State == "burned") { Console.WriteLine($"{disc.Label} is already marked as burned."); return 1; }

        DateTime when = c.Opt.GetDate("date") ?? DateTime.UtcNow;
        disc.State = "burned";
        disc.BurnedUtc = when;
        foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label))
            b.BackedUpUtc = when;

        c.Cat.Save();
        Console.WriteLine($"{disc.Label} marked as burned on {when:yyyy-MM-dd}.");
        return 0;
    }

    // ---- verify ---------------------------------------------------------------------------

    public static int Verify(Ctx c)
    {
        var disc = c.Arc.FindDisc(Need(c, 0, "verify <label> <mountedDiscPath> [--deep]"));
        string mount = Path.GetFullPath(Need(c, 1, "verify <label> <mountedDiscPath> [--deep]"));
        bool deep = c.Opt.Has("deep");
        int ok = 0, bad = 0, miss = 0, unchecked_ = 0;
        bool changed = false;
        KeyCache? keys = null;

        // aliases share one stored copy, so each stored file is checked once
        var items = c.Cat.Files.SelectMany(rec => rec.Backups.Where(bk => bk.Disc == disc.Label))
            .GroupBy(bk => bk.DiscPath, StringComparer.Ordinal).Select(g => g.First()).ToList();

        foreach (var bk in items)
        {
            string p = Path.Combine(mount, bk.DiscPath);
            if (!File.Exists(p)) { Console.WriteLine($"MISSING  {bk.DiscPath}"); miss++; continue; }

            // encrypted copies are checked against the hash of the stored (encrypted) file - no password needed
            string expected = bk.Encrypted ? bk.StoredSha256 ?? "" : bk.ContentSha256;
            if (expected.Length == 0)
            {
                Console.WriteLine($"UNCHECKED {bk.DiscPath}  (no stored hash recorded; use --deep to decrypt and compare)");
                unchecked_++;
            }
            else if (!Hashing.SameHash(Hashing.HashFile(p), expected))
            {
                Console.WriteLine($"CORRUPT  {bk.DiscPath}");
                bad++;
                continue;
            }

            if (deep && bk.Encrypted)
            {
                keys ??= new KeyCache(Ui.GetPassword(c.Opt, forNewEncryption: false));
                if (!DeepCheck(p, bk, keys)) { bad++; continue; }
            }

            if (expected.Length > 0 || deep)
            {
                ok++;
                foreach (var same in c.Cat.Files.SelectMany(f => f.Backups).Where(x => x.Disc == disc.Label && x.DiscPath == bk.DiscPath && x.Unverified))
                {
                    same.Unverified = false;   // now really checked (matters for discs imported with 'scandisc')
                    changed = true;
                }
            }
        }

        Console.WriteLine($"\n{disc.Label}: {ok} OK, {bad} corrupt, {miss} missing, {unchecked_} unchecked (of {items.Count}).");
        if (changed) c.Cat.Save();   // a plain re-check changes nothing and should not use up a history slot
        return bad + miss == 0 ? 0 : 3;
    }

    static bool DeepCheck(string storedPath, BackupRef bk, KeyCache keys)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bdxl-" + Guid.NewGuid().ToString("N"));
        try
        {
            string plain = FileCrypto.Decrypt(storedPath, Path.Combine(tempDir, "x"), keys);
            if (Hashing.SameHash(plain, bk.ContentSha256)) return true;
            Console.WriteLine($"CORRUPT  {bk.DiscPath}  (decrypts, but the content differs from the catalog)");
            return false;
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"CORRUPT  {bk.DiscPath}  ({ex.Message})");
            return false;
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- discard --------------------------------------------------------------------------

    public static int Discard(Ctx c)
    {
        var disc = c.Arc.FindDisc(Need(c, 0, "discard <label>"));
        if (disc.State != "staged") throw new UserException("Only staged (not burned) discs can be discarded.");

        foreach (var f in c.Cat.Files) f.Backups.RemoveAll(b => b.Disc == disc.Label);
        c.Cat.Discs.Remove(disc);
        bool returned = disc.Capacity is not null;
        if (disc.Capacity is { } capacity)   // the blank disc it was going to use is available again
            c.Cat.Blanks.Add(new BlankDisc
            {
                Id = c.Arc.NextBlankId(), Capacity = capacity, Reserve = disc.Reserve ?? 0, Cost = disc.Cost, AddedUtc = DateTime.UtcNow
            });
        c.Cat.Save();
        Console.WriteLine($"{disc.Label} discarded; its files are pending again" + (returned ? " and its blank disc is back in the inventory" : "") +
                          ". (The staged folder, if any, was not deleted.)");
        return 0;
    }

    // ---- discs ----------------------------------------------------------------------------

    public static int Discs(Ctx c)
    {
        if (c.Cat.Discs.Count == 0) { Console.WriteLine("No discs yet."); return 0; }
        string currency = c.Settings.Currency;
        foreach (var d in c.Cat.Discs.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{d.Label}  {d.State,-9}  {d.FileCount,9:N0} files  {d.UsedBytes,18:N0} bytes  created {d.CreatedUtc:yyyy-MM-dd}" +
                              (d.BurnedUtc is { } burnedAt ? $"  burned {burnedAt:yyyy-MM-dd}" : "") +
                              (d.LostUtc is { } lostAt ? $"  {d.State} {lostAt:yyyy-MM-dd}" : "") +
                              (d.RestagedUtc is { } restagedAt ? $"  restaged {restagedAt:yyyy-MM-dd}" : "") +
                              (d.Encrypted ? "  encrypted" : "") +
                              (d.Cost is { } cost ? $"  cost {SizeText.Money(cost, currency)}" : "") +
                              (string.IsNullOrEmpty(d.Note) ? "" : $"  # {d.Note}"));
        return 0;
    }

    // ---- lost / destroyed / found ---------------------------------------------------------

    public static int MarkDead(Ctx c, string state)
    {
        var disc = c.Arc.FindDisc(Need(c, 0, $"{state} <label> [--reason text]"));
        if (disc.State == state) { Console.WriteLine($"{disc.Label} is already marked {state}."); return 1; }
        if (disc.State == "destroyed") throw new UserException($"{disc.Label} is destroyed; that cannot be changed.");

        disc.State = state;
        disc.LostUtc = DateTime.UtcNow;
        string? why = c.Opt.Get("reason");
        if (why != null) disc.Note = why;

        var affected = c.Cat.Files.Where(f => f.Backups.Any(b => b.Disc == disc.Label)).ToList();
        int relinked = c.Arc.LinkDuplicates();   // files whose content also exists on another live disc keep a valid copy
        var dead = c.Arc.DeadDiscs();
        var exposed = affected.Where(f => !c.Arc.HasLiveCopyOfCurrent(f, dead)).ToList();
        var rebackup = exposed.Where(f => f.MissingSinceUtc == null && !f.Ignored).ToList();
        var onSource = c.Arc.SourceHashes();
        var gone = exposed.Where(f => f.MissingSinceUtc != null && !onSource.Contains(f.Sha256)).ToList();
        c.Cat.Save();

        Console.WriteLine($"{disc.Label} marked as {state}. {affected.Count:N0} catalogued file(s) had a copy on it.");
        if (relinked > 0) Console.WriteLine($"{relinked:N0} of them still have an identical copy on another disc.");
        Console.WriteLine($"{rebackup.Count:N0} file(s) are still on the source and are pending again (next 'plan' / 'stage').");
        if (gone.Count > 0)
        {
            Console.WriteLine($"\nDATA LOST - {gone.Count:N0} file(s) no longer exist on the source and have no other copy:");
            foreach (var f in gone.Take(50)) Console.WriteLine($"  {f.RootName}/{f.RelPath}");
            if (gone.Count > 50) Console.WriteLine($"  ... and {gone.Count - 50:N0} more (use 'find' to list)");
            return 2;
        }
        return 0;
    }

    public static int Found(Ctx c)
    {
        var disc = c.Arc.FindDisc(Need(c, 0, "found <label>"));
        if (disc.State != "lost") throw new UserException("Only discs marked 'lost' can be marked as found.");
        disc.State = disc.BurnedUtc != null ? "burned" : "staged";
        disc.LostUtc = null;
        c.Cat.Save();
        Console.WriteLine($"{disc.Label} is '{disc.State}' again. Check it with:  verify {disc.Label} <mountPath>");
        return 0;
    }
}
