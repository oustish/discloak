namespace Bdxl;

/// <summary>
/// A second copy of the catalog in another folder (another drive, a NAS share), kept in step by every save
/// ('settings set mirror &lt;path&gt;'). It holds the current catalog files, catalog.hash.json and bdxl.key - no history -
/// so it is itself a catalog folder that opens with --catalog. Where it is, is written in mirror.json next to
/// bdxl.key (plain: also commands that do not load the catalog, and a damaged catalog, need it).
/// Before a command changes anything, the mirror has to be there, pass its own integrity check and hold exactly
/// what the catalog holds; read-only commands only warn.
/// </summary>
public static class Mirror
{
    public const string FileName = "mirror.json";

    sealed class Config { public string Path { get; set; } = ""; }

    public enum State { None, InSync, Unreachable, Differs, Broken }

    public sealed record Report(State State, string? Path, string Message, long? MainGeneration = null, long? MirrorGeneration = null)
    {
        public bool Ok => State is State.None or State.InSync;
    }

    /// <summary>The configured mirror folder of a catalog, or null.</summary>
    public static string? PathFor(string catalogDir)
    {
        string file = System.IO.Path.Combine(catalogDir, FileName);
        if (!File.Exists(file)) return null;
        try { return JsonStore.ReadObject<Config>(file)?.Path is { Length: > 0 } p ? p : null; }
        catch (JsonException ex) { throw new UserException($"{file} cannot be read ({ex.Message}). Set the mirror again: settings set mirror <path>|none"); }
    }

    /// <summary>Sets (or with null removes) the mirror. The folder must be empty, or hold this same catalog.</summary>
    public static void Configure(string catalogDir, string? mirrorDir)
    {
        string file = System.IO.Path.Combine(catalogDir, FileName);
        if (mirrorDir == null) { if (File.Exists(file)) File.Delete(file); return; }

        string full = System.IO.Path.GetFullPath(mirrorDir).TrimEnd('\\', '/');
        string main = System.IO.Path.GetFullPath(catalogDir).TrimEnd('\\', '/');
        if (full.Equals(main, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(main + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            main.StartsWith(full + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UserException("The mirror must be a folder outside the catalog folder (ideally on another drive).");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            var there = KeyFile.ReadFrom(full);
            var mine = KeyFile.ReadFrom(catalogDir);
            if (there == null) throw new UserException($"{full} is not empty and holds no catalog. Use an empty folder for the mirror.");
            if (mine == null || there.CatalogId != mine.CatalogId)
                throw new UserException($"{full} holds another catalog (id {there.CatalogId}). Use an empty folder for the mirror.");
        }
        Directory.CreateDirectory(full);
        JsonStore.WriteObject(file, new Config { Path = full });
    }

    /// <summary>Compares the catalog in dir with its mirror (keys: to check the mirror's HMAC).</summary>
    public static Report Check(string dir, KeyRing? keys)
    {
        string? path = PathFor(dir);
        if (path == null) return new Report(State.None, null, "no mirror");
        if (!Directory.Exists(path))
            return new Report(State.Unreachable, path, $"the catalog mirror {path} is not reachable (drive not connected?)");
        string mirrorHash = System.IO.Path.Combine(path, Catalog.IntegrityFile);
        if (!File.Exists(mirrorHash))
            return new Report(State.Differs, path, $"the catalog mirror {path} holds no catalog yet");

        var report = Catalog.CheckIntegrity(path, keys);
        var mirror = TryReadManifest(path);
        var mainManifest = TryReadManifest(dir);
        if (report.State == IntegrityState.Broken)
            return new Report(State.Broken, path, $"the catalog mirror {path} fails its integrity check: {string.Join("; ", report.Problems)}",
                              mainManifest?.Generation, mirror?.Generation);

        bool sameFiles = mainManifest != null && mirror != null && mainManifest.Generation == mirror.Generation &&
                         mainManifest.Files.Count == mirror.Files.Count &&
                         mainManifest.Files.All(e => mirror.Files.TryGetValue(e.Key, out var h) && Hashing.SameHash(h, e.Value));
        bool sameKey = SameBytes(KeyFile.PathIn(dir), KeyFile.PathIn(path));
        if (sameFiles && sameKey) return new Report(State.InSync, path, "in step");

        string where = !sameFiles
            ? $"the catalog is at save {mainManifest?.Generation.ToString() ?? "?"}, its mirror at save {mirror?.Generation.ToString() ?? "?"}"
            : "bdxl.key differs (a password change that did not reach the mirror?)";
        return new Report(State.Differs, path, $"the catalog mirror {path} is not in step: {where}", mainManifest?.Generation, mirror?.Generation);
    }

    /// <summary>Throws if a mirror is configured and is not in step (for commands that check it themselves).</summary>
    public static void RequireInSync(string dir, KeyRing? keys)
    {
        var report = Check(dir, keys);
        if (!report.Ok) throw new UserException(Advice(report));
    }

    /// <summary>What to do about a mirror that is not in step.</summary>
    public static string Advice(Report r) => r.State switch
    {
        State.Unreachable => $"Nothing that changes the catalog runs while {r.Message}. Connect it, or stop mirroring with: settings set mirror none",
        State.Broken or State.Differs when r.MirrorGeneration > r.MainGeneration =>
            $"Nothing that changes the catalog runs while {r.Message}. The MIRROR is newer: if the catalog itself is the one that is " +
            "behind (restored or rolled back by hand?), take the mirror's version with 'catalog mirror restore'; to overwrite the " +
            "mirror with this catalog instead: 'catalog mirror sync'.",
        _ => $"Nothing that changes the catalog runs while {r.Message}. Bring the mirror up to date with: catalog mirror sync " +
             "(or, if the catalog itself is damaged: catalog mirror restore)."
    };

    /// <summary>
    /// Copies the catalog in dir to its mirror: every stored file that differs (checked after copying), bdxl.key, then
    /// catalog.hash.json last, and removes stored files the catalog no longer has. Does nothing without a mirror.
    /// </summary>
    public static bool Sync(string dir)
    {
        string? path = PathFor(dir);
        if (path == null) return false;
        if (!Directory.Exists(path))
            throw new UserException($"The catalog was saved, but its mirror {path} is not reachable; connect it and run: catalog mirror sync");
        CopyCatalog(dir, path, trustTarget: true);   // the mirror was checked when the command started
        return true;
    }

    /// <summary>
    /// Copies the catalog files (+ bdxl.key, hash file last) from one catalog folder to another; history stays. A file
    /// is skipped when the target already has it: by the target's own hash list if trustTarget (it was just checked),
    /// otherwise by hashing what is really there (a damaged file may still be listed with its old hash).
    /// </summary>
    public static void CopyCatalog(string from, string to, bool trustTarget = false)
    {
        var source = TryReadManifest(from) ?? throw new UserException($"{from} has no readable {Catalog.IntegrityFile}.");
        var target = TryReadManifest(to);
        var names = Catalog.StoredFilesIn(from);
        foreach (string name in names)
        {
            string dst = System.IO.Path.Combine(to, name);
            bool same = File.Exists(dst) && source.Files.TryGetValue(name, out var want) &&
                        (trustTarget ? target != null && target.Files.TryGetValue(name, out var have) && Hashing.SameHash(want, have)
                                     : Hashing.SameHash(Hashing.HashFile(dst), want));
            if (!same) CopyChecked(System.IO.Path.Combine(from, name), dst, source.Files.GetValueOrDefault(name));
        }
        if (File.Exists(KeyFile.PathIn(from)) && !SameBytes(KeyFile.PathIn(from), KeyFile.PathIn(to)))
            CopyChecked(KeyFile.PathIn(from), KeyFile.PathIn(to), null);
        foreach (string stale in Catalog.StoredFilesIn(to).Except(names, StringComparer.Ordinal))
            File.Delete(System.IO.Path.Combine(to, stale));
        CopyChecked(System.IO.Path.Combine(from, Catalog.IntegrityFile), System.IO.Path.Combine(to, Catalog.IntegrityFile), null);
    }

    static void CopyChecked(string src, string dst, string? expectedHash)
    {
        string tmp = dst + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        string got = Hashing.HashFile(tmp);
        if (!Hashing.SameHash(got, expectedHash ?? Hashing.HashFile(src)))
        {
            File.Delete(tmp);
            throw new IOException($"Copying {System.IO.Path.GetFileName(src)} to {System.IO.Path.GetDirectoryName(dst)} did not read back correctly.");
        }
        File.Move(tmp, dst, overwrite: true);
    }

    static IntegrityManifest? TryReadManifest(string dir)
    {
        string file = System.IO.Path.Combine(dir, Catalog.IntegrityFile);
        try { return File.Exists(file) ? JsonStore.ReadObject<IntegrityManifest>(file) : null; }
        catch (JsonException) { return null; }
    }

    static bool SameBytes(string a, string b) =>
        File.Exists(a) == File.Exists(b) && (!File.Exists(a) || File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)));
}
