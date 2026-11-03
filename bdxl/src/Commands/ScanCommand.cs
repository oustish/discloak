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
    public bool Interruptible => true;
    public string Details => """
        With a folder: adds it to the catalog (first time) or rescans it. Every file gets a SHA-256 hash; unchanged
        files (same size and modified time) are not read again. Files that disappeared are marked as missing.
        A missing file whose identical content turns up under another path counts as moved/renamed: its catalog entry
        (with its copies on discs and its history) moves to the new path.
        Without a folder: rescans ALL known locations. A location that is not reachable (unplugged drive) is
        skipped, nothing is marked as missing.

          --name <alias>   name of the folder on discs and in the catalog (default: the folder name)
          --threads N      hash with N threads (default: 'settings' default-threads, 1)
          --full           ignore size/time and hash everything again
          --catalog <dir>  where the catalog lives (default ./catalog)

        Files matching an ignore rule (see 'ignore') are added to the catalog and marked as ignored, but are not
        hashed and never staged.
        Compression (see 'compression'): for files that will be stored compressed, the same read also counts the exact
        size of their compressed copy, so 'plan' and 'stage' fill discs exactly. When the compression
        setting of a location changes, its unchanged files are read once more to count it again.
        Every hashed file is printed as soon as it is done, with progress by bytes, speed and time left; while large
        files take long, a status line below shows how far they are (rewritten in place, not written when the output
        is redirected). The catalog is saved every 2 minutes.
        A file that changes while it is being read (still being written to) is skipped and left as it was; the next
        scan reads it again. Sizes are taken from the file itself, not the folder listing (which Windows can show out
        of date for hard-linked files).
        Ctrl+C stops cleanly: the files being hashed are dropped, what was hashed is saved, nothing is marked as
        missing in that location, and the summary says what was left; running the scan again continues. A second
        Ctrl+C stops at once.
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
        public int Level;                          // > 0: also count the compressed stored size at this zstd level
        public long Stored;                        // that size
        public bool CountOnly;                     // unchanged, read again only to count the compressed size
        public string? Error;
        public long BytesRead;                     // written by the hashing thread, read for progress
    }

    static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(2);   // refresh of the status line while large files are hashed
    const string BeingWritten = "it changed while it was being read (still being written to?) - left as it was, scan again later";

    sealed class Totals
    {
        public int Added, Changed, Unchanged, Skipped, Missing, Moved, IgnoredNew, BecameIgnored, NoLongerIgnored, Counted;
        public bool Interrupted;                   // Ctrl+C: stopped early
        public int Unhashed;                       // files that still needed hashing when it stopped
        public long UnhashedBytes;
        public bool WalkCut;                       // stopped while still listing folders
        public List<string> NotScanned = new();    // locations not reached
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
        DateTime started = DateTime.UtcNow;

        foreach (var (root, name) in roots)
        {
            if (!Directory.Exists(root))
            {
                // a disconnected drive must not turn every file into "missing"
                Console.WriteLine($"Warning: {name} ({root}) is not available - skipped, nothing marked as missing.");
                continue;
            }
            if (totals.Interrupted) { totals.NotScanned.Add(name); continue; }
            ScanRoot(c, root, name, threads, full, totals);
        }

        DetectMoves(c, started, totals);
        int linked = c.Arc.LinkDuplicates();
        c.Cat.Save();

        Console.WriteLine($"\nScan {(totals.Interrupted ? "INTERRUPTED" : "done")}: {totals.Added} new, {totals.Changed} changed, {totals.Unchanged} unchanged, " +
                          $"{totals.Moved} moved/renamed, {totals.Missing} no longer on source, {totals.Skipped} skipped (unreadable).");
        if (totals.IgnoredNew + totals.BecameIgnored > 0)
            Console.WriteLine($"Ignored by rules: {totals.IgnoredNew} new, {totals.BecameIgnored} existing files now ignored (they stay in the catalog but are not staged).");
        if (totals.NoLongerIgnored > 0)
            Console.WriteLine($"{totals.NoLongerIgnored} file(s) are no longer ignored.");
        if (linked > 0)
            Console.WriteLine($"{linked} file(s) already backed up under another path (same hash) - linked, nothing to burn.");
        if (totals.Counted > 0)
            Console.WriteLine($"{totals.Counted} unchanged file(s) were read again to count their compressed size (the compression setting changed).");
        if (!totals.Interrupted) return 0;

        Console.WriteLine($"Stopped by Ctrl+C. What was hashed is saved; {totals.Unhashed:N0} file(s), {SizeText.Bytes(totals.UnhashedBytes)} bytes " +
                          "were not hashed yet" + (totals.WalkCut ? " (not counting folders it had not listed yet)" : "") + (totals.NotScanned.Count > 0 ? $", and these locations were not scanned: {string.Join(", ", totals.NotScanned)}" : "") + ".");
        Console.WriteLine("Nothing was marked as missing in the location that was interrupted. Run the same scan again to finish: " +
                          "files already hashed are not read again.");
        return Interrupt.ExitCode;
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
        while (folders.Count > 0 && !Interrupt.Requested)
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
                int level = Compression.LevelFor(c.Settings, name, rel);
                bool needsCount = !needsHash && level > 0 && rec!.CompressedWith != Compression.Signature(level);
                if (!needsHash && !needsCount)
                {
                    rec!.MissingSinceUtc = null;
                    t.Unchanged++;
                    continue;
                }
                work.Add(new WorkItem
                {
                    Index = work.Count, FullPath = fi.FullName, Rel = rel, Size = size, Mtime = mtime, Rec = rec, Level = level, CountOnly = !needsHash
                });
            }
        }

        // 2. hash with the requested number of threads, each taking the next file as soon as it is free; the results
        //    are applied to the catalog (and printed) on this thread, one by one, in the order they finish
        long totalBytes = work.Sum(w => w.Size), doneBytes = 0;
        if (work.Count > 0)
            Console.WriteLine($"  {work.Count:N0} file(s), {SizeText.Bytes(totalBytes)} bytes to read");
        var results = new BlockingCollection<WorkItem>();
        var active = new ConcurrentDictionary<WorkItem, byte>();   // being hashed right now
        int next = -1, running = threads;
        var workers = work.Count == 0 ? [] : Enumerable.Range(0, threads).Select(worker => Task.Factory.StartNew(() =>
        {
            try
            {
                int i;
                while (!Interrupt.Requested && (i = Interlocked.Increment(ref next)) < work.Count)
                {
                    var w = work[i];
                    active[w] = 0;
                    try
                    {
                        // the folder listing can show an old size (hard links, files another program keeps open): take
                        // the size from the file itself, or the progress runs past 100% and the record gets a wrong size
                        var (size, mtime) = SizeAndTime(w.FullPath);
                        if (size != w.Size) { Interlocked.Add(ref totalBytes, size - w.Size); w.Size = size; }
                        void Progress(long read)
                        {
                            Volatile.Write(ref w.BytesRead, read);
                            if (Interrupt.Requested) throw new OperationCanceledException();   // a large file stops mid-way
                            if (read > w.Size) throw new IOException(BeingWritten);            // growing: it would be read without end
                        }
                        if (w.Level > 0) (w.Hash, w.Stored) = StoredForm.HashAndMeasure(w.FullPath, w.Level, Progress);
                        else w.Hash = Hashing.HashFile(w.FullPath, Progress);
                        if (Volatile.Read(ref w.BytesRead) != w.Size || SizeAndTime(w.FullPath) != (size, mtime))
                            throw new IOException(BeingWritten);   // the hash would be of neither the old nor the new content
                        results.Add(w);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { w.Error = ex.Message; results.Add(w); }
                    catch (OperationCanceledException) { }
                    finally { active.TryRemove(w, out _); }
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
                    else if (w.CountOnly) t.Counted++;
                    else t.Unchanged++;

                    rec.Size = w.Size;
                    rec.ModifiedUtc = w.Mtime;
                    rec.Sha256 = hash;
                    rec.MissingSinceUtc = null;
                    rec.Ignored = false;
                    rec.CompressedSize = w.Level > 0 ? w.Stored : null;
                    rec.CompressedWith = w.Level > 0 ? Compression.Signature(w.Level) : null;
                    string compressed = w.Level > 0 ? $", compressed to {100.0 * w.Stored / Math.Max(1, w.Size):F0}%" : "";
                    Console.WriteLine($"  [{Progress(done, work.Count, doneBytes, Interlocked.Read(ref totalBytes), active.Keys, clock.Elapsed)}] {w.Rel} ({SizeText.Bytes(w.Size)} bytes{compressed})");
                }
            }
            if (clock.Elapsed - lastStatus >= StatusEvery && !active.IsEmpty)
            {
                string head = $"  [{Progress(done, work.Count, doneBytes, Interlocked.Read(ref totalBytes), active.Keys, clock.Elapsed)}] ";
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

        if (Interrupt.Requested)   // the walk or the hashing was cut short: what was not seen may still be there
        {
            t.Interrupted = true;
            t.WalkCut |= folders.Count > 0;
            t.Unhashed += work.Count - done;
            t.UnhashedBytes += totalBytes - doneBytes;
            return;
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

    /// <summary>
    /// A file that is gone from where it was, whose identical content (same hash) is now at a path that has no copy of
    /// its own on any disc, was moved or renamed: its record takes the new path - keeping its copies, older versions
    /// and history - and the record for the new path (new in this scan, or only linked to copies of other files)
    /// is dropped. Otherwise the new path would get a pointer to the old copy and the old path would stay listed as
    /// gone forever. A moved file that was also changed has another hash and is not recognised.
    /// </summary>
    static void DetectMoves(Ctx c, DateTime started, Totals t)
    {
        var gone = c.Cat.Files.Where(f => f.MissingSinceUtc != null && f.Sha256.Length > 0)
                              .GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0) return;

        var replaced = new HashSet<FileRecord>();
        var report = new List<string>();
        foreach (var found in c.Cat.Files.Where(f => f.MissingSinceUtc == null && !f.Ignored && f.Sha256.Length > 0 && f.Backups.All(b => b.Alias)))
        {
            if (!gone.TryGetValue(found.Sha256, out var candidates) || candidates.Count == 0) continue;
            string fileName = Path.GetFileName(found.RelPath);
            var old = candidates.OrderByDescending(x => Path.GetFileName(x.RelPath).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                                .ThenByDescending(x => x.Root == found.Root)
                                .ThenByDescending(x => x.MissingSinceUtc)
                                .First();
            candidates.Remove(old);

            report.Add($"  moved: {old.RootName}/{old.RelPath}  ->  {found.RootName}/{found.RelPath}");
            if (old.MissingSinceUtc >= started) t.Missing--;   // it went missing in this scan: it did not really disappear
            if (found.AddedUtc >= started) t.Added--;
            t.Moved++;
            (old.Root, old.RootName, old.RelPath, old.Size, old.ModifiedUtc) = (found.Root, found.RootName, found.RelPath, found.Size, found.ModifiedUtc);
            old.MissingSinceUtc = null;
            foreach (var link in found.Backups.Where(a => !old.Backups.Any(b => b.Disc == a.Disc && b.At == a.At && Hashing.SameHash(b.Sha256, a.Sha256))))
                old.Backups.Add(link);   // links to copies of other files that the old record did not have yet
            replaced.Add(found);
        }
        if (replaced.Count == 0) return;

        c.Cat.Files.RemoveAll(replaced.Contains);
        Console.WriteLine();
        foreach (string line in report.Take(50)) Console.WriteLine(line);
        if (report.Count > 50) Console.WriteLine($"  ... and {report.Count - 50:N0} more");
    }

    /// <summary>Size and last write time as the file itself has them (not the folder listing, which can be behind).</summary>
    static (long Size, DateTime Mtime) SizeAndTime(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return (RandomAccess.GetLength(h), File.GetLastWriteTimeUtc(h));
    }

    /// <summary>"1,234/50,000  12.4%  85.3 MB/s  ~3h 12m left" - by bytes, counting what the threads have read of their current files.</summary>
    static string Progress(int done, int count, long doneBytes, long totalBytes, IEnumerable<WorkItem> active, TimeSpan elapsed) =>
        $"{StatusLine.Count(done, count)}  {StatusLine.Rate(doneBytes + active.Sum(a => Volatile.Read(ref a.BytesRead)), totalBytes, elapsed)}";

    const int MinFileWidth = 24;   // narrower than this a shortened path says too little

    /// <summary>
    /// The files being hashed, as "path (34.6%)", in room characters: the room is shared equally, long paths are
    /// shortened in the middle (start and file name stay), and if not all fit, the ones closest to finishing are
    /// shown, followed by "+N more".
    /// </summary>
    static string ActiveFiles(List<WorkItem> active, int room)
    {
        var items = active.Select(a => (a.Rel, Pct: StatusLine.Percent(Volatile.Read(ref a.BytesRead), a.Size), Left: a.Size - Volatile.Read(ref a.BytesRead)))
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
}
