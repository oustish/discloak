namespace Bdxl;

public sealed class VerifyCommand : ICommand
{
    public string Name => "verify";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Re-read a disc and compare it with the catalog";
    public string Usage => "verify <label> <mountedDiscPath> [--deep] [--password-env VAR]";
    public bool Interruptible => true;
    public string Details => """
        Hashes every file on the disc. Encrypted files are checked against the hash of the stored file, so no
        password is needed; --deep decrypts them as well and compares the original content.
        Entries recorded by 'scandisc' without hashing are marked 'unverified' until this has run.
        Ctrl+C stops after the file being read and reports what was checked so far.
        """;

    public int Run(Ctx c)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, "verify <label> <mountedDiscPath> [--deep]"));
        string mount = Path.GetFullPath(c.Arg(1, "verify <label> <mountedDiscPath> [--deep]"));
        bool deep = c.Opt.Has("deep");
        int ok = 0, bad = 0, miss = 0, unchecked_ = 0;
        bool changed = false;
        KeyCache? keys = null;

        // aliases share one stored copy, so each stored file is checked once
        var items = c.Cat.Files.SelectMany(rec => rec.Backups.Where(bk => bk.Disc == disc.Label))
            .GroupBy(bk => bk.DiscPath, StringComparer.Ordinal).Select(g => g.First()).ToList();

        int finished = 0;   // files fully checked, whatever the outcome
        foreach (var bk in items)
        {
            if (Interrupt.Requested) break;
            finished++;
            string p = Path.Combine(mount, bk.DiscPath);
            if (!File.Exists(p)) { Console.WriteLine($"MISSING  {bk.DiscPath}"); miss++; continue; }

            // encrypted copies are checked against the hash of the stored (encrypted) file - no password needed
            string expected = bk.Encrypted ? bk.StoredSha256 ?? "" : bk.ContentSha256;
            if (expected.Length == 0)
            {
                Console.WriteLine($"UNCHECKED {bk.DiscPath}  (no stored hash recorded; use --deep to decrypt and compare)");
                unchecked_++;
            }
            else
            {
                string got = HashOrStop(p);
                if (Interrupt.Requested) { finished--; break; }   // cut off in the middle: not judged
                if (!Hashing.SameHash(got, expected))
                {
                    Console.WriteLine($"CORRUPT  {bk.DiscPath}");
                    bad++;
                    continue;
                }
            }

            if (deep && bk.Encrypted)
            {
                keys ??= new KeyCache(Ui.GetPassword(c.Opt, forNewEncryption: false));
                if (!DeepCheck(p, bk, keys)) { bad++; continue; }
            }
            if (deep && bk.Encrypted && Interrupt.Requested) { finished--; break; }   // decrypting was cut off: not judged

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
        if (Interrupt.Requested)
        {
            Console.WriteLine($"Stopped by Ctrl+C: {items.Count - finished:N0} of {items.Count:N0} file(s) were not checked.");
            return Interrupt.ExitCode;
        }
        return bad + miss == 0 ? 0 : 3;
    }

    // "" when Ctrl+C cut the hash off; the caller stops right after it
    static string HashOrStop(string path)
    {
        try { return Hashing.HashFile(path, Interrupt.Token); }
        catch (OperationCanceledException) { return ""; }
    }

    static bool DeepCheck(string storedPath, BackupRef bk, KeyCache keys)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bdxl-" + Guid.NewGuid().ToString("N"));
        try
        {
            string plain = FileCrypto.Decrypt(storedPath, Path.Combine(tempDir, "x"), keys, Interrupt.Token);
            if (Hashing.SameHash(plain, bk.ContentSha256)) return true;
            Console.WriteLine($"CORRUPT  {bk.DiscPath}  (decrypts, but the content differs from the catalog)");
            return false;
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"CORRUPT  {bk.DiscPath}  ({ex.Message})");
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;   // not judged: the loop stops right after it
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
