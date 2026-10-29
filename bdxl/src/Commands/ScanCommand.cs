using System.Collections.Concurrent;
using System.Diagnostics;

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
        Every hashed file is printed as soon as it is done, with progress by bytes, speed and time left; while large
        files take long, a status line below shows how far they are (rewritten in place, not written when the output
        is redirected). The catalog is saved every 2 minutes.
        """;

    sealed class WorkItem
    {
        public int Index;                          // position in the folder walk (keeps the catalog order deterministic)
        public string FullPath = "";
        public string Rel = "";
        public long Size;
        public DateTime Mtime;
        public FileRecord? Rec;
        public string? Hash;
        public string? Error;
        public long BytesRead;                     // written by the hashing thread, read for progress
    }

    static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(2);   // refresh of the status line while large files are hashed

    /// <summary>
    /// One line at the bottom of the console, rewritten in place (the files already done stay printed above it).
    /// Nothing is written when the output goes to a file or pipe.
    /// </summary>
    sealed class StatusLine
    {
        readonly bool live = !Console.IsOutputRedirected;
        int width;

        public void Show(string text)
        {
            if (!live) return;
            int max = Math.Max(20, WindowWidth() - 1);   // a wrapped line could not be overwritten
            if (text.Length > max) text = text[..(max - 3)] + "...";
            Console.Write("\r" + text.PadRight(width));
            width = Math.Max(width, text.Length);
        }

        public void Clear()
        {
            if (width == 0) return;
            Console.Write("\r" + new string(' ', width) + "\r");
            width = 0;
        }

        public static int WindowWidth()
        {
            try { return Console.WindowWidth > 0 ? Console.WindowWidth : 120; }
            catch (IOException) { return 120; }
        }
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
                work.Add(new WorkItem { Index = work.Count, FullPath = fi.FullName, Rel = rel, Size = size, Mtime = mtime, Rec = rec });
            }
        }

        // 2. hash with the requested number of threads, each taking the next file as soon as it is free; the results
        //    are applied to the catalog (and printed) on this thread, one by one, in the order they finish
        long totalBytes = work.Sum(w => w.Size), doneBytes = 0;
        if (work.Count > 0)
            Console.WriteLine($"  {work.Count:N0} file(s), {SizeText.Bytes(totalBytes)} bytes to hash");
        var results = new BlockingCollection<WorkItem>();
        var active = new ConcurrentDictionary<WorkItem, byte>();   // being hashed right now
        int next = -1, running = threads;
        var workers = work.Count == 0 ? [] : Enumerable.Range(0, threads).Select(worker => Task.Factory.StartNew(() =>
        {
            try
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < work.Count)
                {
                    var w = work[i];
                    active[w] = 0;
                    try { w.Hash = Hashing.HashFile(w.FullPath, read => Volatile.Write(ref w.BytesRead, read)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { w.Error = ex.Message; }
                    active.TryRemove(w, out _);
                    results.Add(w);
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref running) == 0) results.CompleteAdding();
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        if (workers.Length == 0) results.CompleteAdding();

        int done = 0;
        var added = new List<(int Index, FileRecord Rec)>();
        var clock = Stopwatch.StartNew();
        var status = new StatusLine();
        TimeSpan lastStatus = TimeSpan.Zero;
        DateTime lastSave = DateTime.UtcNow;
        while (!results.IsCompleted)
        {
            if (results.TryTake(out var w, TimeSpan.FromMilliseconds(500)))
            {
                status.Clear();
                done++;
                doneBytes += w.Size;
                if (w.Error != null)
                {
                    Console.WriteLine($"  skipped {w.Rel}: {w.Error}");
                    t.Skipped++;
                }
                else
                {
                    string hash = w.Hash!;
                    var rec = w.Rec;
                    if (rec == null)
                    {
                        rec = new FileRecord { Root = root, RootName = name, RelPath = w.Rel, AddedUtc = now };
                        c.Cat.Files.Add(rec);
                        added.Add((w.Index, rec));
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
                    Console.WriteLine($"  [{Progress(done, work.Count, doneBytes, totalBytes, active.Keys, clock.Elapsed)}] {w.Rel} ({SizeText.Bytes(w.Size)} bytes)");
                }
            }
            if (clock.Elapsed - lastStatus >= StatusEvery && !active.IsEmpty)
            {
                string head = $"  [{Progress(done, work.Count, doneBytes, totalBytes, active.Keys, clock.Elapsed)}] ";
                status.Show(head + ActiveFiles(active.Keys.ToList(), StatusLine.WindowWidth() - 1 - head.Length));
                lastStatus = clock.Elapsed;
            }

            if (DateTime.UtcNow - lastSave > TimeSpan.FromMinutes(2))   // don't lose hours of hashing on a crash
            {
                c.Cat.Save();
                lastSave = DateTime.UtcNow;
            }
        }
        status.Clear();
        Task.WaitAll(workers);

        // new files were added in the order they finished: put them back in folder order, as with one thread
        if (threads > 1 && added.Count > 1)
        {
            var newOnes = added.Select(a => a.Rec).ToHashSet();
            c.Cat.Files.RemoveAll(newOnes.Contains);
            c.Cat.Files.AddRange(added.OrderBy(a => a.Index).Select(a => a.Rec));
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

    /// <summary>"1,234/50,000  12.4%  85.3 MB/s  ~3h 12m left" - by bytes, counting what the threads have read of their current files.</summary>
    static string Progress(int done, int count, long doneBytes, long totalBytes, IEnumerable<WorkItem> active, TimeSpan elapsed)
    {
        long read = doneBytes + active.Sum(a => Volatile.Read(ref a.BytesRead));
        double perSecond = elapsed.TotalSeconds > 0 ? read / elapsed.TotalSeconds : 0;
        // fixed widths, so the status does not move from line to line ("100.0%", speeds up to 9999.9 MB/s)
        string countText = count.ToString("N0", CultureInfo.InvariantCulture);
        string text = $"{done.ToString("N0", CultureInfo.InvariantCulture).PadLeft(countText.Length)}/{countText}  " +
                      $"{Percent(read, totalBytes),6}  {(perSecond / 1e6).ToString("F1", CultureInfo.InvariantCulture),6} MB/s";
        if (perSecond > 0 && read < totalBytes && elapsed > TimeSpan.FromSeconds(5))
            text += $"  ~{Duration(TimeSpan.FromSeconds((totalBytes - read) / perSecond))} left";
        return text;
    }

    const int MinFileWidth = 24;   // narrower than this a shortened path says too little

    /// <summary>
    /// The files being hashed, as "path (34.6%)", in room characters: the room is shared equally, long paths are
    /// shortened in the middle (start and file name stay), and if not all fit, the ones closest to finishing are
    /// shown, followed by "+N more".
    /// </summary>
    static string ActiveFiles(List<WorkItem> active, int room)
    {
        var items = active.Select(a => (a.Rel, Pct: Percent(Volatile.Read(ref a.BytesRead), a.Size), Left: a.Size - Volatile.Read(ref a.BytesRead)))
                          .OrderBy(x => x.Left).ToList();
        string Label(string rel, string pct) => $"{rel} ({pct})";
        string all = string.Join(", ", items.Select(x => Label(x.Rel, x.Pct)));
        if (all.Length <= room || items.Count == 0) return all;

        int shown = items.Count;
        string more = "";
        while (shown > 1 && (room - more.Length) / shown - 2 < MinFileWidth)
        {
            shown--;
            more = $", +{items.Count - shown} more";
        }
        int each = Math.Max(MinFileWidth, (room - more.Length) / shown - 2);   // 2 = ", "
        return string.Join(", ", items.Take(shown).Select(x => Label(SizeText.ShortenMiddle(x.Rel, each - x.Pct.Length - 3), x.Pct))) + more;
    }

    static string Percent(long part, long whole) =>
        (whole > 0 ? 100.0 * part / whole : 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";

    static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m" :
        d.TotalMinutes >= 1 ? $"{d.Minutes}m {d.Seconds:D2}s" : $"{d.Seconds}s";
}
