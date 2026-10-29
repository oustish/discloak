namespace Bdxl;

/// <summary>scan [folder] [--name alias] [--threads N] [--full]  -  index files; without a folder every known location is rescanned.</summary>
public sealed class ScanCommand : ICommand
{
    public string Name => "scan";
    public string Group => CommandGroup.Indexing;
    public string Summary => "Index files (a folder, or every known location)";
    public string Usage => "scan [<folder>] [--name <alias>] [--threads N] [--full]";
    public string Details => """
        With a folder: adds it to the catalog (first time) or rescans it. Every file gets a SHA-256 hash; unchanged
        files (same size and modified time) are not read again. Files that disappeared are marked as missing.
        Without a folder: rescans ALL known locations. A location that is not reachable (unplugged drive) is
        skipped, nothing is marked as missing.

          --name <alias>   name of the folder on discs and in the catalog (default: the folder name)
          --threads N      hash with N threads (default: 'settings' default-threads, 1)
          --full           ignore size/time and hash everything again
          --catalog <dir>  where the catalog lives (default ./catalog)

        Files matching an ignore rule (see 'ignore') are added to the catalog and marked as ignored, but are not
        hashed and never staged.
        """;

    sealed class WorkItem
    {
        public string FullPath = "";
        public string Rel = "";
        public long Size;
        public DateTime Mtime;
        public FileRecord? Rec;
        public string? Hash;
        public string? Error;
    }

    sealed class Totals
    {
        public int Added, Changed, Unchanged, Skipped, Missing, IgnoredNew, BecameIgnored, NoLongerIgnored;
    }

    static string NormalizeRoot(string arg)
    {
        string root = Path.GetFullPath(arg);
        if (root.Length > Path.GetPathRoot(root)!.Length)
            root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return root;
    }

    public int Run(Ctx c)
    {
        if (c.Args.Count > 1) throw new UserException("scan takes at most one folder.");

        var roots = new List<(string Root, string Name)>();
        bool rescanAll = c.Args.Count == 0;

        if (!rescanAll)
        {
            string root = NormalizeRoot(c.Args[0]);
            if (!Directory.Exists(root)) throw new UserException($"Not a directory: {root}");

            string? known = c.Cat.Files.Where(f => f.Root == root).Select(f => f.RootName).FirstOrDefault();
            string? requested = c.Opt.Get("name");
            if (known != null && requested != null && !known.Equals(requested, StringComparison.OrdinalIgnoreCase))
                throw new UserException($"This folder is already catalogued as '{known}'. To rename it use:  alias rename {known} {requested}");

            string name = requested ?? known ?? Path.GetFileName(root);
            if (string.IsNullOrEmpty(name))
                throw new UserException("Cannot derive a name from this path; use --name <alias>.");
            if (name.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new UserException("The alias must not contain / or \\.");
            if (c.Cat.Files.Any(f => f.RootName.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Root != root))
                throw new UserException($"A different folder is already catalogued as '{name}'. Use --name <alias>.");
            roots.Add((root, name));
        }
        else
        {
            roots = c.Cat.Files.Where(f => !f.Root.StartsWith("imported:", StringComparison.Ordinal))
                .GroupBy(f => f.Root)
                .Select(g => (Root: g.Key, Name: g.First().RootName))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (roots.Count == 0)
                throw new UserException("No locations are known yet. Scan one first:  scan <folder> [--name alias]");
            Console.WriteLine($"Rescanning {roots.Count} known location(s).");
        }

        int threads = Math.Clamp(c.Opt.GetInt("threads", c.Settings.DefaultThreads), 1, 64);
        bool full = c.Opt.Has("full");
        var totals = new Totals();

        foreach (var (root, name) in roots)
        {
            if (!Directory.Exists(root))
            {
                // a disconnected drive must not turn every file into "missing"
                Console.WriteLine($"Warning: {name} ({root}) is not available - skipped, nothing marked as missing.");
                continue;
            }
            ScanRoot(c, root, name, threads, full, totals);
        }

        int linked = c.Arc.LinkDuplicates();
        c.Cat.Save();

        Console.WriteLine($"\nScan done: {totals.Added} new, {totals.Changed} changed, {totals.Unchanged} unchanged, " +
                          $"{totals.Missing} no longer on source, {totals.Skipped} skipped (unreadable).");
        if (totals.IgnoredNew + totals.BecameIgnored > 0)
            Console.WriteLine($"Ignored by rules: {totals.IgnoredNew} new, {totals.BecameIgnored} existing files now ignored (they stay in the catalog but are not staged).");
        if (totals.NoLongerIgnored > 0)
            Console.WriteLine($"{totals.NoLongerIgnored} file(s) are no longer ignored.");
        if (linked > 0)
            Console.WriteLine($"{linked} file(s) already backed up under another path (same hash) - linked, nothing to burn.");
        return 0;
    }

    static void ScanRoot(Ctx c, string root, string name, int threads, bool full, Totals t)
    {
        Console.WriteLine($"\nScanning {name}  ({root})  using {threads} thread(s)");
        var ignore = c.Arc.BuildIgnore();
        var byPath = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        foreach (var f in c.Cat.Files)
            if (f.Root == root) byPath.TryAdd(f.RelPath, f);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failedDirs = new List<string>();
        var work = new List<WorkItem>();
        DateTime now = DateTime.UtcNow;

        // 1. walk the folder (sequential); decide what has to be hashed
        var folders = new Stack<(DirectoryInfo Dir, string Rel)>();
        folders.Push((new DirectoryInfo(root), ""));
        while (folders.Count > 0)
        {
            var (dir, relDir) = folders.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failedDirs.Add(relDir);
                Console.WriteLine($"  cannot read folder {(relDir.Length == 0 ? "." : relDir)}: {ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // symlinks / junctions
                string rel = relDir.Length == 0 ? entry.Name : relDir + "/" + entry.Name;

                if (entry is DirectoryInfo sub) { folders.Push((sub, rel)); continue; }
                if (entry is not FileInfo fi) continue;

                seen.Add(rel);
                long size;
                DateTime mtime;
                try { size = fi.Length; mtime = fi.LastWriteTimeUtc; }
                catch (IOException) { t.Skipped++; continue; }

                byPath.TryGetValue(rel, out var rec);

                if (ignore.IsIgnored(rel))
                {
                    // ignored files are catalogued (so they are visible) but not hashed and never staged
                    if (rec == null)
                    {
                        rec = new FileRecord { Root = root, RootName = name, RelPath = rel, AddedUtc = now, Ignored = true };
                        c.Cat.Files.Add(rec);
                        byPath[rel] = rec;
                        t.IgnoredNew++;
                    }
                    else if (!rec.Ignored)
                    {
                        rec.Ignored = true;   // was pending or already burned: no new backup from now on
                        t.BecameIgnored++;
                    }
                    rec.Size = size;
                    rec.ModifiedUtc = mtime;
                    rec.MissingSinceUtc = null;
                    continue;
                }

                if (rec != null && rec.Ignored) { rec.Ignored = false; t.NoLongerIgnored++; }

                bool needsHash = full || rec == null || rec.Sha256.Length == 0 || rec.Size != size || rec.ModifiedUtc != mtime;
                if (!needsHash)
                {
                    rec!.MissingSinceUtc = null;
                    t.Unchanged++;
                    continue;
                }
                work.Add(new WorkItem { FullPath = fi.FullName, Rel = rel, Size = size, Mtime = mtime, Rec = rec });
            }
        }

        // 2. hash in batches with the requested number of threads; apply results one batch at a time
        int batchSize = Math.Max(16, threads * 4);
        int done = 0;
        DateTime lastSave = DateTime.UtcNow;
        for (int start = 0; start < work.Count; start += batchSize)
        {
            var slice = work.GetRange(start, Math.Min(batchSize, work.Count - start));
            Parallel.ForEach(slice, new ParallelOptions { MaxDegreeOfParallelism = threads }, w =>
            {
                try { w.Hash = Hashing.HashFile(w.FullPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { w.Error = ex.Message; }
            });

            foreach (var w in slice)
            {
                done++;
                if (w.Error != null)
                {
                    Console.WriteLine($"  skipped {w.Rel}: {w.Error}");
                    t.Skipped++;
                    continue;
                }

                string hash = w.Hash!;
                var rec = w.Rec;
                if (rec == null)
                {
                    rec = new FileRecord { Root = root, RootName = name, RelPath = w.Rel, AddedUtc = now };
                    c.Cat.Files.Add(rec);
                    byPath[w.Rel] = rec;
                    t.Added++;
                }
                else if (rec.Sha256.Length == 0) t.Added++;
                else if (!Hashing.SameHash(rec.Sha256, hash)) t.Changed++;
                else t.Unchanged++;

                rec.Size = w.Size;
                rec.ModifiedUtc = w.Mtime;
                rec.Sha256 = hash;
                rec.MissingSinceUtc = null;
                rec.Ignored = false;
                Console.WriteLine($"  [{done}/{work.Count}] {w.Rel} ({SizeText.Bytes(w.Size)} bytes)");
            }

            if (DateTime.UtcNow - lastSave > TimeSpan.FromMinutes(2))   // don't lose hours of hashing on a crash
            {
                c.Cat.Save();
                lastSave = DateTime.UtcNow;
            }
        }

        // 3. files that vanished (but never guess about folders we could not read)
        foreach (var rec in c.Cat.Files)
        {
            if (rec.Root != root || rec.MissingSinceUtc != null || seen.Contains(rec.RelPath)) continue;
            if (failedDirs.Any(d => d.Length == 0 || rec.RelPath.StartsWith(d + "/", StringComparison.Ordinal))) continue;
            rec.MissingSinceUtc = now;
            t.Missing++;
        }
    }
}
