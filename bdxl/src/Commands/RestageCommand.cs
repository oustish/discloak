namespace Bdxl;

public sealed class RestageCommand : ICommand
{
    public string Name => "restage";
    public string Group => CommandGroup.Recovery;
    public string Summary => "Rebuild a disc's folder (same label, same files) to burn it again";
    public string Usage => "restage <label> <destDir> [--from dirA;dirB] [--allow-partial]";
    public bool Interruptible => true;
    public string Details => """
        Writes the disc's packs again, with the same layout: every file at the same place in the data stream. A pack
        is copied as it is from a --from folder (the old disc, a copy of it, or an interrupted restage) if its hash
        matches; any other pack is rebuilt from the current source files (where their hash still matches) and
        encrypted again with the disc's own key. Files that are ignored now are included: the disc is rebuilt the
        way it was. Every rebuilt file is checked against its hash. A disc with appended sessions is rebuilt as one
        folder with one manifest. Afterwards the disc is 'staged' until you run 'burned'.
        Without --allow-partial nothing is staged if any file cannot be rebuilt; with it, such files are left out
        (their place in the pack stays unused) and no longer count as being on the disc.
        Ctrl+C stops after the pack being written; the catalog stays unchanged and what was rebuilt is kept as
        <destDir>/<label>.partial, to be reused with --from on the next run.
        """;

    /// <summary>What an interrupted restage leaves in its folder: the packs written so far that hold only checked files.</summary>
    sealed record Progress(string Label, Dictionary<int, string> Packs);
    const string ProgressName = "restage.json";

    public int Run(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: " + Usage);
        var disc = c.Arc.FindDisc(c.Args[0]);
        if (disc.PendingAppend is { } session)
            throw new UserException($"Session {session.Number} of {disc.Label} is staged but not burned: burn it ('burned {disc.Label}') " +
                                    $"or take it back ('discard {disc.Label}') first.");
        string dir = Path.Combine(Path.GetFullPath(c.Args[1]), disc.Label);
        if (Directory.Exists(dir)) throw new UserException($"Folder already exists: {dir}");
        var fromDirs = (c.Opt.Get("from") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool allowPartial = c.Opt.Has("allow-partial");

        var entries = c.Arc.CopiesOn(disc.Label);
        if (entries.Count == 0) throw new UserException($"The catalog has no files for {disc.Label}.");
        if (disc.KeyId != null && !c.Keys.Contains(disc.KeyId))
            throw new UserException($"{disc.Label} is encrypted with a key this catalog does not have (key id {disc.KeyId}).");
        var key = c.Keys.For(disc.KeyId);   // rebuilt with the disc's own key, as it was
        int packs = Math.Max(disc.Packs.Count, DiscFormat.PackOf(entries[^1].Copy.End - 1));
        var onSource = c.Cat.Files.Where(f => f.MissingSinceUtc == null && f.Sha256.Length > 0).ToLookup(f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var reuse = fromDirs.Select(d => (Dir: d, Progress: ReadProgress(d, disc.Label))).ToList();

        Directory.CreateDirectory(dir);
        var hashes = new string[packs];
        var sources = new Dictionary<BackupRef, PieceSource?>();   // pieces being rebuilt; null = no source
        var failed = new HashSet<BackupRef>();                     // pieces that could not be rebuilt
        var rebuiltIn = new Dictionary<int, List<BackupRef>>();    // rebuilt pack -> the pieces in it
        var progress = new Progress(disc.Label, new Dictionary<int, string>());
        var buffer = new byte[DiscFormat.PackData];
        int copied = 0, rebuilt = 0, next = 0;   // next: first entry not yet behind the current pack

        try
        {
            for (int p = 1; p <= packs && !Interrupt.Requested; p++)
            {
                long start = (p - 1) * DiscFormat.PackData, end = start + DiscFormat.PackData;
                var here = entries.Skip(next).TakeWhile(e => e.Copy.At < end).Where(e => e.Copy.End > start && e.Copy.StoredSize > 0).ToList();

                if (TryReuse(p, out string? sha))
                {
                    hashes[p - 1] = sha!;
                    progress.Packs[p] = sha!;
                    copied++;
                    Console.WriteLine($"[{disc.Label}] pack {p} of {packs}: taken over");
                }
                else
                {
                    Array.Clear(buffer);
                    foreach (var (file, copy) in here)
                    {
                        if (failed.Contains(copy)) continue;
                        if (!sources.TryGetValue(copy, out var source)) sources[copy] = source = PieceSource.Open(copy, onSource);
                        if (source == null) { Fail(copy, file, "no source file with identical content"); continue; }
                        long from = Math.Max(copy.At, start), to = Math.Min(copy.End, end);
                        try { source.Read(from - copy.At, buffer.AsSpan((int)(from - start), (int)(to - from))); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Fail(copy, file, ex.Message); }
                    }
                    using (var writer = new PackWriter(dir, p, key))
                    {
                        writer.Write(buffer);
                        hashes[p - 1] = writer.Written.Single().Sha256;
                    }
                    rebuiltIn[p] = here.Select(e => e.Copy).ToList();   // a failed piece keeps the pack from ever being taken over
                    rebuilt++;
                    Console.WriteLine($"[{disc.Label}] pack {p} of {packs}: rebuilt");
                }

                // pieces that end in this pack are complete: check them against their hash
                foreach (var (file, copy) in here.Where(e => e.Copy.End <= end))
                    if (sources.Remove(copy, out var done) && done != null)
                        using (done)
                            if (done.Finish() is { } problem) Fail(copy, file, problem);
                while (next < entries.Count && entries[next].Copy.End <= end) next++;

                // a rebuilt pack is safe to take over later once every piece in it has been checked
                foreach (var (pack, inIt) in rebuiltIn.Where(kv => !progress.Packs.ContainsKey(kv.Key)).ToList())
                    if (inIt.All(cp => !sources.ContainsKey(cp) && !failed.Contains(cp)))
                        progress.Packs[pack] = hashes[pack - 1];
                JsonStore.WriteObject(Path.Combine(dir, ProgressName), progress);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var s in sources.Values) s?.Dispose();
        }

        if (Interrupt.Requested)   // the catalog stays as it was; what was rebuilt can be picked up again with --from
        {
            string kept = dir + ".partial";
            for (int n = 2; Directory.Exists(kept); n++) kept = $"{dir}.partial{n}";
            Directory.Move(dir, kept);
            Console.WriteLine($"\nStopped by Ctrl+C after {copied + rebuilt:N0} of {packs:N0} packs. The catalog is unchanged." +
                              (progress.Packs.Count > 0 ? $" To reuse them, run restage again with  --from \"{kept}\"" : ""));
            return Interrupt.ExitCode;
        }
        File.Delete(Path.Combine(dir, ProgressName));

        if (failed.Count > 0)
        {
            Console.WriteLine($"\n{failed.Count:N0} file(s) could not be rebuilt (no source file with identical content, no intact pack under --from):");
            foreach (var (file, copy) in entries.Where(e => failed.Contains(e.Copy)).Take(50)) Console.WriteLine($"  {file.RootName}/{file.RelPath}");
            if (!allowPartial)
            {
                Directory.Delete(dir, true);
                Console.WriteLine("\nNothing staged and the catalog is unchanged. Add --from <folder;folder> to point at a copy of the disc, " +
                                  "or --allow-partial to restage without those files.");
                return 3;
            }
            foreach (var f in c.Cat.Files) f.Backups.RemoveAll(b => b.Disc == disc.Label && failed.Any(x => x.At == b.At));
        }

        var onDisc = c.Arc.CopiesOn(disc.Label);
        if (onDisc.Count == 0)
        {
            Directory.Delete(dir, true);
            throw new UserException("No file could be rebuilt; the catalog is unchanged.");
        }

        disc.State = "staged";           // it has to be burned again
        disc.BurnedUtc = null;
        disc.LostUtc = null;
        disc.RestagedUtc = DateTime.UtcNow;
        disc.FileCount = onDisc.Count;
        disc.UsedBytes = onDisc.Sum(x => x.Copy.StoredSize);
        disc.Packs = hashes.ToList();
        disc.Sessions = 1;               // the whole disc (all its sessions) is in one folder now
        disc.KeyId = key.KeyId;
        foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label)) b.BackedUpUtc = null;

        var manifest = onDisc.Select(x => ManifestLine.For(x.Copy, x.File)).ToList();
        DiscFiles.Write(dir, disc, manifest, key, c.Cat.Dir, session: 1);
        disc.OccupiedBytes = Math.Max(packs * DiscFormat.PackOnDisc + DiscFiles.ManifestSpace(manifest) + (disc.Reserve ?? 0), DiscSpace.Folder(dir));
        c.Arc.LinkDuplicates();
        c.Cat.Save();
        Console.WriteLine($"\n{disc.Label}: {onDisc.Count:N0} files in {packs:N0} packs ({copied:N0} taken over, {rebuilt:N0} rebuilt) in {dir}");
        Console.WriteLine($"Burn it, then run:  burned {disc.Label}");
        return 0;

        void Fail(BackupRef copy, FileRecord file, string why)
        {
            if (!failed.Add(copy)) return;
            Console.WriteLine($"  cannot rebuild {file.RootName}/{file.RelPath}: {why}");
            if (sources.Remove(copy, out var s)) s?.Dispose();
            sources[copy] = null;
        }

        // pack p from a --from folder, if it is the disc's own (its hash is in the catalog) or one a restage of this
        // disc wrote and checked (in that folder's restage.json)
        bool TryReuse(int p, out string? sha)
        {
            sha = null;
            foreach (var (from, done) in reuse)
            {
                string src = Path.Combine(from, DiscFormat.PackPath(p)), dst = Path.Combine(dir, DiscFormat.PackPath(p));
                if (!File.Exists(src)) continue;
                try
                {
                    string got = Hashing.CopyAndHash(src, dst, ct: Interrupt.Token);
                    bool known = p <= disc.Packs.Count && Hashing.SameHash(got, disc.Packs[p - 1]);
                    if (known || (done != null && done.TryGetValue(p, out string? listed) && Hashing.SameHash(got, listed)))
                    {
                        sha = got.ToUpperInvariant();
                        return true;
                    }
                    File.Delete(dst);
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"  {src}: {ex.Message}");
                    if (File.Exists(dst)) File.Delete(dst);
                }
            }
            return false;
        }
    }

    static Dictionary<int, string>? ReadProgress(string dir, string label)
    {
        try
        {
            var p = JsonStore.ReadObject<Progress>(Path.Combine(dir, ProgressName));
            return p != null && p.Label.Equals(label, StringComparison.OrdinalIgnoreCase) ? p.Packs : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The stored form of one copy, made again from a source file with the same content (the same compression level
    /// gives the same bytes), read forward. Finish reads what is left and checks the whole copy against its hash.
    /// </summary>
    sealed class PieceSource : IDisposable
    {
        readonly BackupRef copy;
        readonly FileStream input;
        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        readonly ZstdSharp.Compressor? zstd;
        readonly byte[] block = new byte[StoredForm.Block];
        byte[] pending = [];
        int pendingAt;
        long position, plainRead;   // stored bytes handed out; plaintext bytes read

        PieceSource(BackupRef copy, FileStream input)
        {
            this.copy = copy;
            this.input = input;
            if (copy.Compression > 0) zstd = new ZstdSharp.Compressor(copy.Compression);
        }

        /// <summary>A source for the copy: the first source file with its content that is still as scanned (null = none).</summary>
        public static PieceSource? Open(BackupRef copy, ILookup<string, FileRecord> onSource)
        {
            foreach (var f in onSource[copy.Sha256].OrderByDescending(f => SameAsScanned(f)))
            {
                try
                {
                    var input = new FileStream(Path.Combine(f.Root, f.RelPath), FileMode.Open, FileAccess.Read, FileShare.Read, StoredForm.Block, FileOptions.SequentialScan);
                    input.Seek(copy.Offset, SeekOrigin.Begin);
                    return new PieceSource(copy, input);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return null;
        }

        static bool SameAsScanned(FileRecord f)
        {
            var fi = new FileInfo(Path.Combine(f.Root, f.RelPath));
            return fi.Exists && fi.Length == f.Size && fi.LastWriteTimeUtc == f.ModifiedUtc;
        }

        /// <summary>Stored bytes from `offset` on (never before what was handed out) into target.</summary>
        public void Read(long offset, Span<byte> target)
        {
            if (offset < position) throw new InvalidOperationException("a piece is read forward only");
            Skip(offset - position);
            while (target.Length > 0)
            {
                if (pendingAt == pending.Length && !NextBlock()) throw new InvalidDataException("the source file is shorter than the copy");
                int n = Math.Min(target.Length, pending.Length - pendingAt);
                pending.AsSpan(pendingAt, n).CopyTo(target);
                pendingAt += n;
                position += n;
                target = target[n..];
            }
        }

        void Skip(long bytes)
        {
            while (bytes > 0)
            {
                if (pendingAt == pending.Length && !NextBlock()) throw new InvalidDataException("the source file is shorter than the copy");
                int n = (int)Math.Min(bytes, pending.Length - pendingAt);
                pendingAt += n;
                position += n;
                bytes -= n;
            }
        }

        bool NextBlock()
        {
            int want = (int)Math.Min(StoredForm.Block, copy.Length - plainRead);
            if (want <= 0) return false;   // read to the end: checking it needs nothing more (not even after Ctrl+C)
            Interrupt.Token.ThrowIfCancellationRequested();
            int n = input.ReadAtLeast(block.AsSpan(0, want), want, throwOnEndOfStream: false);
            if (n == 0) return false;
            hash.AppendData(block, 0, n);
            plainRead += n;
            pending = zstd != null ? zstd.Wrap(block.AsSpan(0, n)).ToArray() : block.AsSpan(0, n).ToArray();
            pendingAt = 0;
            return true;
        }

        /// <summary>Reads the rest; null if the copy came out exactly as stored, else what is wrong.</summary>
        public string? Finish()
        {
            try { Skip(copy.StoredSize - position); }
            catch (InvalidDataException ex) { return ex.Message; }
            if (pendingAt != pending.Length || NextBlock()) return "the source file is longer than the copy";
            return Hashing.SameHash(Convert.ToHexString(hash.GetHashAndReset()), copy.ContentSha256) ? null : "the source file's content differs";
        }

        public void Dispose()
        {
            input.Dispose();
            hash.Dispose();
            zstd?.Dispose();
        }
    }
}
