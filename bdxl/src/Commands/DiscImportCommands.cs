namespace Bdxl;

/// <summary>scandisc (record an already burned disc in the catalog) and discjson (write disc.json onto a disc that still accepts data)</summary>
public static class DiscImportCommands
{
    static DriveInfo? FindDrive(string path)
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => path.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static bool IsDriveRoot(string path, DriveInfo d) =>
        string.Equals(path.TrimEnd('\\', '/'), d.RootDirectory.FullName.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static bool IsDiscMeta(string rel) =>
        rel.Equals("disc.json", StringComparison.OrdinalIgnoreCase) || rel.Equals("manifest.jsonl", StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith("_catalog/", StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith("System Volume Information/", StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith("$RECYCLE.BIN/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a mounted disc (or any folder laid out like one) and records it in the catalog.
    ///   --method auto|manifest|hash|quick   auto = manifest.jsonl if the disc has one, otherwise hash
    ///   --label X          label to use (otherwise disc.json, otherwise the drive's volume label)
    ///   --date yyyy-MM-dd  when it was burned      --capacity 100GB   size of the disc      --cost 24.99   what it cost
    ///   --update           the label is already in the catalog: merge what is on the disc
    ///   --dry-run          show what would happen, change nothing
    ///   --write-disc-json  if the disc can still be written to, put disc.json (+ manifest.jsonl) on it
    /// </summary>
    public static int ScanDisc(Ctx c)
    {
        if (c.Args.Count != 1) throw new UserException("Usage: scandisc <mountPath> [--label L] [--method auto|manifest|hash|quick] [--date D] [--capacity S] [--cost P] [--update] [--dry-run] [--write-disc-json]");
        string mount = Path.GetFullPath(c.Args[0]);
        if (!Directory.Exists(mount)) throw new UserException($"Not a directory: {mount}");
        bool dry = c.Opt.Has("dry-run");
        string method = c.Opt.GetOr("method", "auto").ToLowerInvariant();
        if (method is not ("auto" or "manifest" or "hash" or "quick"))
            throw new UserException("--method must be auto, manifest, hash or quick.");
        var drive = FindDrive(mount);

        // 1. which disc is this?
        DiscInfo? info = null;
        string infoPath = Path.Combine(mount, "disc.json");
        if (File.Exists(infoPath))
        {
            try { info = JsonStore.ReadObject<DiscInfo>(infoPath); }
            catch (JsonException) { Console.WriteLine("disc.json cannot be parsed - ignoring it."); }
        }
        string? label = c.Opt.Get("label");
        string how = "--label";
        if (label == null && info != null) { label = info.Label; how = "disc.json"; }
        if (label == null && drive != null && IsDriveRoot(mount, drive))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)) { label = drive.VolumeLabel.Trim(); how = "volume label"; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (string.IsNullOrWhiteSpace(label))
            throw new UserException("Cannot tell which disc this is (no disc.json, no volume label). Pass --label <label>.");
        if (label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new UserException($"'{label}' is not usable as a label.");
        if (info != null && !info.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"Warning: disc.json says {info.Label}, but the label {label} was requested.");

        var existing = c.Cat.Discs.FirstOrDefault(d => d.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !c.Opt.Has("update"))
            throw new UserException($"{existing.Label} is already in the catalog. Use --update to merge what is on this disc, or 'verify' to check it.");
        label = existing?.Label ?? label;
        Console.WriteLine($"Disc {label} (label from {how}){(existing != null ? ", updating its existing record" : "")}");

        DateTime burnedAt = c.Opt.GetDate("date") ?? existing?.BurnedUtc ?? info?.CreatedUtc ?? DateTime.UtcNow;

        // 2. what is on it?
        var onDisc = new List<(string Path, long Size, DateTime Mtime)>();
        var enumOpts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var fi in new DirectoryInfo(mount).EnumerateFiles("*", enumOpts))
        {
            string rel = Path.GetRelativePath(mount, fi.FullName).Replace('\\', '/');
            if (!IsDiscMeta(rel)) onDisc.Add((rel, fi.Length, fi.LastWriteTimeUtc));
        }

        string manifestPath = Path.Combine(mount, "manifest.jsonl");
        if (method == "auto") method = File.Exists(manifestPath) ? "manifest" : "hash";
        if (method == "manifest" && !File.Exists(manifestPath))
            throw new UserException("There is no manifest.jsonl on this disc; use --method hash or quick.");
        Console.WriteLine($"Method: {method}; {onDisc.Count:N0} file(s), {SizeText.Bytes(onDisc.Sum(x => x.Size))} bytes on the disc.");

        var found = new List<DiscFile>();
        var problems = new List<string>();      // listed in the manifest but missing / different size, or unreadable
        var notListed = new List<string>();     // on the disc but not in the manifest

        if (method == "hash")
        {
            foreach (var f in onDisc)
            {
                Console.WriteLine($"hashing {f.Path} ({SizeText.Bytes(f.Size)} bytes)");
                try { found.Add(new DiscFile(f.Path, f.Size, f.Mtime, Hashing.HashFile(Path.Combine(mount, f.Path)), false)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{f.Path}: {ex.Message}"); }
            }
            if (onDisc.Any(f => f.Path.EndsWith(FileCrypto.Suffix, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine("Note: *.enc files look encrypted; hashing cannot see their original content. Use the disc's manifest.jsonl (--method manifest) to link them to known files.");
            if (onDisc.Any(f => Regex.IsMatch(f.Path, @"\.\d{3,}(\.enc)?$")))
                Console.WriteLine("Note: *.001, *.002 ... files may be parts of split files; only the disc's manifest.jsonl (--method manifest) records them as parts.");
        }
        else if (method == "manifest")
        {
            var diskByPath = onDisc.ToDictionary(x => x.Path, x => x, StringComparer.Ordinal);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in JsonStore.ReadLines<ManifestLine>(manifestPath))
            {
                listed.Add(line.Path);
                if (!diskByPath.TryGetValue(line.Path, out var onThisDisc)) { problems.Add($"{line.Path}: listed in the manifest but not on the disc"); continue; }
                if (onThisDisc.Size != line.Size) { problems.Add($"{line.Path}: size differs from the manifest"); continue; }
                bool isPart = line is { Part: > 0, Parts: > 0, Offset: not null, Length: not null, FileSha256: not null };
                if (line.Encrypted && line.PlainSha256 != null)
                    found.Add(new DiscFile(line.Path, onThisDisc.Size, onThisDisc.Mtime, (isPart ? line.FileSha256! : line.PlainSha256).ToUpperInvariant(),
                                           true, true, line.Sha256.ToUpperInvariant()) { SplitLine = isPart ? line : null });
                else
                    found.Add(new DiscFile(line.Path, onThisDisc.Size, onThisDisc.Mtime, (isPart ? line.FileSha256! : line.Sha256).ToUpperInvariant(), true)
                              { SplitLine = isPart ? line : null });
            }
            notListed = onDisc.Where(x => !listed.Contains(x.Path)).Select(x => x.Path).ToList();
        }
        else   // quick
        {
            foreach (var f in onDisc) found.Add(new DiscFile(f.Path, f.Size, f.Mtime, null, true));
        }

        // 3. connect what was found with what the catalog already knows
        var byPathIdx = c.Cat.Files.ToLookup(f => f.RootName + "/" + f.RelPath, StringComparer.Ordinal);
        var byHashIdx = c.Cat.Files.Where(f => f.Sha256.Length > 0).ToLookup(f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var disc = existing ?? new DiscRecord
        {
            Label = label, State = "burned", CreatedUtc = burnedAt, BurnedUtc = burnedAt, Note = $"imported from disc ({method})"
        };
        var manifest = new List<ManifestLine>();
        var unmatched = new List<string>();
        int sameVersion = 0, olderVersion = 0, otherPath = 0, created = 0, already = 0;

        foreach (var df in found)
        {
            FileRecord? rec;
            string sha;
            // the catalog knows the file without ".enc" and without a part's ".001"
            string logical = df.Encrypted && df.Path.EndsWith(FileCrypto.Suffix, StringComparison.OrdinalIgnoreCase)
                ? df.Path[..^FileCrypto.Suffix.Length] : df.Path;
            if (df.SplitLine is { } split)
            {
                string suffix = SplitLayout.Suffix(split.Part!.Value, split.Parts!.Value);
                if (logical.EndsWith(suffix, StringComparison.Ordinal)) logical = logical[..^suffix.Length];
            }
            var candidates = byPathIdx[logical].ToList();

            if (df.Sha256 == null)   // quick: nothing was hashed, so the catalog entry has to match on path, size and time
            {
                rec = candidates.FirstOrDefault(x => x.Size == df.Size && Math.Abs((x.ModifiedUtc - df.Mtime).TotalSeconds) <= 2);
                if (rec == null) { unmatched.Add(df.Path); continue; }
                sha = rec.Sha256;
                sameVersion++;
            }
            else
            {
                sha = df.Sha256;
                rec = candidates.FirstOrDefault(x => Hashing.SameHash(x.Sha256, sha));
                if (rec != null) sameVersion++;
                else if (candidates.Count > 0) { rec = candidates[0]; olderVersion++; }   // same path, other content: an older/newer version
                else
                {
                    rec = byHashIdx[sha].FirstOrDefault();                                   // same content under another path (moved/renamed)
                    if (rec != null) otherPath++;
                }

                if (rec == null)   // not known at all: create an entry (the source file is not available)
                {
                    int slash = logical.IndexOf('/');
                    string rootName = slash > 0 ? logical[..slash] : "(imported)";
                    string relPath = slash > 0 ? logical[(slash + 1)..] : logical;
                    string root = c.Cat.Files.FirstOrDefault(f => f.RootName == rootName && !f.Root.StartsWith("imported:", StringComparison.Ordinal))?.Root
                                  ?? "imported:" + label;
                    rec = new FileRecord
                    {
                        Root = root, RootName = rootName, RelPath = relPath,
                        Size = df.SplitLine?.FileSize ?? (df.Encrypted ? Math.Max(0, df.Size - 36) : df.Size),
                        ModifiedUtc = df.Mtime, Sha256 = sha, AddedUtc = DateTime.UtcNow, MissingSinceUtc = DateTime.UtcNow
                    };
                    c.Cat.Files.Add(rec);
                    created++;
                }
            }

            var copy = new BackupRef
            {
                Disc = label, DiscPath = df.Path, Sha256 = sha, BackedUpUtc = burnedAt, Unverified = df.Unverified,
                Encrypted = df.Encrypted, StoredSha256 = df.StoredSha256
            };
            if (df.SplitLine is { } part)
            {
                copy.Part = part.Part!.Value; copy.Parts = part.Parts!.Value; copy.Offset = part.Offset!.Value; copy.Length = part.Length!.Value;
                copy.PartSha256 = (part.Encrypted ? part.PlainSha256 ?? "" : part.Sha256).ToUpperInvariant();
            }
            manifest.Add(ManifestLine.For(copy, df.Size, rec.Size));
            if (rec.Backups.Any(b => b.Disc == label && b.DiscPath == df.Path)) { already++; continue; }
            rec.Backups.Add(copy);
        }
        int linked = c.Arc.LinkDuplicates();   // other catalog files with identical content are now covered by this disc too

        disc.FileCount = onDisc.Count;
        disc.UsedBytes = onDisc.Sum(x => x.Size);
        if (found.Any(x => x.Encrypted)) disc.Encrypted = true;
        string? capText = c.Opt.Get("capacity");
        if (capText != null) disc.Capacity = SizeText.Parse(capText);
        decimal? cost = c.Opt.GetMoney("cost");
        if (cost != null) disc.Cost = cost;

        // 4. report
        Console.WriteLine($"\n{label}: {found.Count - unmatched.Count:N0} of {onDisc.Count:N0} file(s) recorded.");
        Console.WriteLine($"  matched to known files: {sameVersion:N0} same version, {olderVersion:N0} other version of the same path, {otherPath:N0} same content under another path");
        Console.WriteLine($"  new catalog entries (not seen in any source scan): {created:N0}");
        if (already > 0) Console.WriteLine($"  already recorded for this disc: {already:N0}");
        if (linked > 0) Console.WriteLine($"  {linked:N0} more catalog file(s) have identical content and now count as backed up");
        if (unmatched.Count > 0)
            Console.WriteLine($"  {unmatched.Count:N0} file(s) could not be matched without hashing - re-run with --method hash (e.g. {unmatched[0]})");
        if (notListed.Count > 0)
            Console.WriteLine($"  {notListed.Count:N0} file(s) are on the disc but not in its manifest - re-run with --method hash to include them (e.g. {notListed[0]})");
        foreach (var p in problems.Take(20)) Console.WriteLine("  PROBLEM " + p);
        if (found.Any(x => x.Unverified))
            Console.WriteLine($"  Entries taken without hashing are marked unverified; 'verify {label} <mount>' will check them.");

        long? free = null;
        try { free = drive?.AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (free is { } freeBytes)
            Console.WriteLine($"  free space reported for this drive: {SizeText.Bytes(freeBytes)} bytes" +
                              (freeBytes > (64L << 20) ? " - the disc may still accept more data" : " - looks full or finalized"));

        if (dry) { Console.WriteLine("\nDry run - the catalog was not changed."); return 0; }

        if (existing == null) c.Cat.Discs.Add(disc);
        if (c.Opt.Has("write-disc-json"))
        {
            if (TryWriteDiscJson(mount, disc, manifest, c.Opt.Has("overwrite"), out string why))
                Console.WriteLine("\ndisc.json (and manifest.jsonl if it was missing) written onto the disc. If your burning software needs it, close the session/disc now.");
            else
                Console.WriteLine($"\nCould not write disc.json: {why}. The label is recorded in the catalog only.");
        }
        c.Cat.Save();
        return problems.Count > 0 ? 3 : 0;
    }

    static bool TryWriteDiscJson(string mount, DiscRecord disc, List<ManifestLine> manifest, bool overwrite, out string why)
    {
        string infoPath = Path.Combine(mount, "disc.json");
        if (File.Exists(infoPath) && !overwrite) { why = "disc.json is already there (use --overwrite to replace it)"; return false; }

        var drive = FindDrive(mount);
        try
        {
            if (drive != null && drive.AvailableFreeSpace < (1L << 20)) { why = "the disc has no free space left (it looks finalized)"; return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try
        {
            JsonStore.WriteObjectDirect(infoPath, new DiscInfo(disc.Label, disc.CreatedUtc, disc.FileCount, disc.UsedBytes));
            string manifestPath = Path.Combine(mount, "manifest.jsonl");
            if (!File.Exists(manifestPath) && manifest.Count > 0)
                File.WriteAllLines(manifestPath, manifest.Select(m => JsonSerializer.Serialize(m, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = "the disc cannot be written to (" + ex.Message + ")";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>discjson &lt;label&gt; &lt;mountPath&gt;: writes disc.json (and manifest.jsonl if missing) onto a disc that can still be written to.</summary>
    public static int DiscJson(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: discjson <label> <mountPath> [--overwrite]");
        var disc = c.Arc.FindDisc(c.Args[0]);
        string mount = Path.GetFullPath(c.Args[1]);
        if (!Directory.Exists(mount)) throw new UserException($"Not a directory: {mount}");

        var manifest = c.Cat.Files.SelectMany(f => f.Backups.Where(b => b.Disc == disc.Label).Select(b => (File: f, Copy: b)))
            .GroupBy(x => x.Copy.DiscPath, StringComparer.Ordinal).Select(g => g.First())
            .Select(x => (x.File, x.Copy, FullPath: Path.Combine(mount, x.Copy.DiscPath)))
            .Where(x => File.Exists(x.FullPath))
            .Select(x => ManifestLine.For(x.Copy, new FileInfo(x.FullPath).Length, x.File.Size))
            .ToList();

        if (TryWriteDiscJson(mount, disc, manifest, c.Opt.Has("overwrite"), out string why))
        {
            Console.WriteLine($"disc.json written for {disc.Label} ({manifest.Count:N0} file(s) found on the disc).");
            return 0;
        }
        Console.WriteLine($"Could not write disc.json: {why}.");
        return 3;
    }
}
