namespace Bdxl;

public sealed class ScanDiscCommand : ICommand
{
    public string Name => "scandisc";
    public string Group => CommandGroup.ExistingDiscs;
    public string Summary => "Record an already burned disc in the catalog";
    public string Usage => "scandisc <mountPath> [--label L] [--method auto|manifest|hash|quick] [--date D] [--capacity S] [--cost P] [--update] [--dry-run] [--write-disc-json]";
    public bool Interruptible => true;
    public string Details => """
        Reads a mounted disc (or a folder laid out like one) and links its files to the catalog.
          --method manifest  trust the disc's manifest (fast; entries stay 'unverified' until 'verify'). An encrypted
                             disc's manifest (manifest.jsonl.enc) is read with the catalog's master key; it is the
                             only way to link the hidden file names of such a disc. A disc of another catalog needs
                             that catalog (or one made with 'init --key-from <disc>').
          --method hash      read and SHA-256 every file (slow, authoritative, finds files nobody listed)
          --method quick     no hashing: match path + size + time against the catalog
          --method auto      manifest if the disc has one, otherwise hash (default)
        The label comes from --label, else disc.json, else the drive's volume label.
        Files the catalog does not know yet get new entries. --update merges into a disc already in the catalog.
        --write-disc-json writes disc.json (+ manifest.jsonl) onto the disc if it can still be written to
        (open session / not finalized); otherwise the label is recorded in the catalog only.
        Ctrl+C while hashing stops without recording anything (the catalog stays unchanged).
        """;

    /// <summary>
    /// Reads a mounted disc (or any folder laid out like one) and records it in the catalog.
    ///   --method auto|manifest|hash|quick   auto = manifest.jsonl if the disc has one, otherwise hash
    ///   --label X          label to use (otherwise disc.json, otherwise the drive's volume label)
    ///   --date yyyy-MM-dd  when it was burned      --capacity 100GB   size of the disc      --cost 24.99   what it cost
    ///   --update           the label is already in the catalog: merge what is on the disc
    ///   --dry-run          show what would happen, change nothing
    ///   --write-disc-json  if the disc can still be written to, put disc.json (+ manifest.jsonl) on it
    /// </summary>
    public int Run(Ctx c)
    {
        if (c.Args.Count != 1) throw new UserException("Usage: scandisc <mountPath> [--label L] [--method auto|manifest|hash|quick] [--date D] [--capacity S] [--cost P] [--update] [--dry-run] [--write-disc-json]");
        string mount = Path.GetFullPath(c.Args[0]);
        if (!Directory.Exists(mount)) throw new UserException($"Not a directory: {mount}");
        bool dry = c.Opt.Has("dry-run");
        string method = c.Opt.GetOr("method", "auto").ToLowerInvariant();
        if (method is not ("auto" or "manifest" or "hash" or "quick"))
            throw new UserException("--method must be auto, manifest, hash or quick.");
        var drive = DiscFiles.FindDrive(mount);

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

        bool hasManifest = DiscFiles.HasManifest(mount);
        if (method == "auto") method = hasManifest ? "manifest" : "hash";
        if (method == "manifest" && !hasManifest)
            throw new UserException("There is no manifest on this disc; use --method hash or quick.");
        if (method != "manifest" && File.Exists(Path.Combine(mount, DiscFiles.EncryptedManifestName)))
            Console.WriteLine("Note: this disc hides its file names; only its encrypted manifest (--method manifest) links them to real paths.");
        Console.WriteLine($"Method: {method}; {onDisc.Count:N0} file(s), {SizeText.Bytes(onDisc.Sum(x => x.Size))} bytes on the disc.");

        var found = new List<DiscFile>();
        var problems = new List<string>();      // listed in the manifest but missing / different size, or unreadable
        var notListed = new List<string>();     // on the disc but not in the manifest

        if (method == "hash")
        {
            foreach (var f in onDisc)
            {
                if (Interrupt.Requested) break;
                Console.WriteLine($"hashing {f.Path} ({SizeText.Bytes(f.Size)} bytes)");
                try { found.Add(new DiscFile(f.Path, f.Size, f.Mtime, Hashing.HashFile(Path.Combine(mount, f.Path), Interrupt.Token), false)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{f.Path}: {ex.Message}"); }
                catch (OperationCanceledException) { break; }
            }
            if (Interrupt.Requested)   // a disc recorded with only some of its files would look complete
            {
                Console.WriteLine($"\nStopped by Ctrl+C after hashing {found.Count:N0} of {onDisc.Count:N0} file(s). Nothing was recorded; " +
                                  "the catalog is unchanged. Run scandisc again to read the whole disc.");
                return Interrupt.ExitCode;
            }
            if (onDisc.Any(f => f.Path.EndsWith(FileCrypto.Suffix, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine("Note: *.enc files look encrypted; hashing cannot see their original content. Use the disc's manifest (--method manifest) to link them to known files.");
            if (onDisc.Any(f => Regex.IsMatch(f.Path, @"\.\d{3,}(\.enc)?$")))
                Console.WriteLine("Note: *.001, *.002 ... files may be parts of split files; only the disc's manifest (--method manifest) records them as parts.");
        }
        else if (method == "manifest")
        {
            var diskByPath = onDisc.ToDictionary(x => x.Path, x => x, StringComparer.Ordinal);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in DiscFiles.ReadManifest(mount, c.Keys)!)
            {
                listed.Add(line.Path);
                if (!diskByPath.TryGetValue(line.Path, out var onThisDisc)) { problems.Add($"{line.Path}: listed in the manifest but not on the disc"); continue; }
                if (onThisDisc.Size != line.Size) { problems.Add($"{line.Path}: size differs from the manifest"); continue; }
                bool isPart = line is { Part: > 0, Parts: > 0, Offset: not null, Length: not null, FileSha256: not null };
                if (line.Encrypted && line.PlainSha256 != null)
                    found.Add(new DiscFile(line.Path, onThisDisc.Size, onThisDisc.Mtime, (isPart ? line.FileSha256! : line.PlainSha256).ToUpperInvariant(),
                                           true, true, line.Sha256.ToUpperInvariant()) { SplitLine = isPart ? line : null, Source = line.Source, Compression = line.Compression ?? 0 });
                else
                    found.Add(new DiscFile(line.Path, onThisDisc.Size, onThisDisc.Mtime, (isPart ? line.FileSha256! : line.Sha256).ToUpperInvariant(), true)
                              { SplitLine = isPart ? line : null, Source = line.Source, Compression = line.Compression ?? 0 });
            }
            notListed = onDisc.Where(x => !listed.Contains(x.Path)).Select(x => x.Path).ToList();
        }
        else   // quick
        {
            foreach (var f in onDisc) found.Add(new DiscFile(f.Path, f.Size, f.Mtime, null, true));
        }

        // 3. connect what was found with what the catalog already knows
        var byPathIdx = c.Cat.Files.GroupBy(f => f.RootName + "/" + f.RelPath, StringComparer.Ordinal)
                                   .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var byHashIdx = c.Cat.Files.Where(f => f.Sha256.Length > 0).ToLookup(f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var createdHere = new HashSet<FileRecord>();   // a disc with appended sessions can hold several versions of one path
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
            // the catalog knows the file without ".enc" and without a part's ".001" (a hidden name: from the manifest)
            string logical = df.Encrypted && df.Path.EndsWith(FileCrypto.Suffix, StringComparison.OrdinalIgnoreCase)
                ? df.Path[..^FileCrypto.Suffix.Length] : df.Path;
            if (df.Source != null) logical = df.Source;
            else if (df.SplitLine is { } split)
            {
                string suffix = SplitLayout.Suffix(split.Part!.Value, split.Parts!.Value);
                if (logical.EndsWith(suffix, StringComparison.Ordinal)) logical = logical[..^suffix.Length];
            }
            var candidates = byPathIdx.TryGetValue(logical, out var known) ? known : [];
            long fileSize = df.SplitLine?.FileSize ?? (df.Encrypted ? FileCrypto.MaxPlainSize(df.Size) : df.Size);

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
                else if (candidates.Count > 0)                                           // same path, other content: an older/newer version
                {
                    rec = candidates[0];
                    olderVersion++;
                    // only known from this disc: the later copy (source time, or staging time if hidden) is the current version
                    if (createdHere.Contains(rec) && df.Mtime > rec.ModifiedUtc)
                        (rec.Sha256, rec.Size, rec.ModifiedUtc) = (sha, fileSize, df.Mtime);
                }
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
                        Size = fileSize, ModifiedUtc = df.Mtime, Sha256 = sha, AddedUtc = DateTime.UtcNow, MissingSinceUtc = DateTime.UtcNow
                    };
                    c.Cat.Files.Add(rec);
                    if (known == null) byPathIdx[logical] = [rec];
                    else known.Add(rec);
                    createdHere.Add(rec);
                    created++;
                }
            }

            var copy = new BackupRef
            {
                Disc = label, DiscPath = df.Path, Sha256 = sha, BackedUpUtc = burnedAt, Unverified = df.Unverified,
                Encrypted = df.Encrypted, StoredSha256 = df.StoredSha256, StoredSize = df.Size, Compression = df.Compression
            };
            if (df.SplitLine is { } part)
            {
                copy.Part = part.Part!.Value; copy.Parts = part.Parts!.Value; copy.Offset = part.Offset!.Value; copy.Length = part.Length!.Value;
                copy.PartSha256 = (part.Encrypted ? part.PlainSha256 ?? "" : part.Sha256).ToUpperInvariant();
            }
            manifest.Add(ManifestLine.For(copy, df.Size, rec));
            if (rec.Backups.Any(b => b.Disc == label && b.DiscPath == df.Path)) { already++; continue; }
            rec.Backups.Add(copy);
        }
        int linked = c.Arc.LinkDuplicates();   // other catalog files with identical content are now covered by this disc too

        disc.FileCount = onDisc.Count;
        disc.UsedBytes = onDisc.Sum(x => x.Size);
        if (found.Any(x => x.Encrypted)) disc.Encrypted = true;
        if (disc.Encrypted) disc.KeyId ??= info?.KeyId ?? (File.Exists(Path.Combine(mount, DiscFiles.EncryptedManifestName)) ? FileCrypto.KeyIdOf(Path.Combine(mount, DiscFiles.EncryptedManifestName)) : null);
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
            if (DiscFiles.TryWriteDiscJson(mount, disc, manifest, c.Opt.Has("overwrite"), c.Keys.For(disc.Encrypted ? disc.KeyId : null), c.Cat.Dir, out string why))
                Console.WriteLine("\ndisc.json (and the manifest if it was missing) written onto the disc. If your burning software needs it, close the session/disc now.");
            else
                Console.WriteLine($"\nCould not write disc.json: {why}. The label is recorded in the catalog only.");
        }
        c.Cat.Save();
        return problems.Count > 0 ? 3 : 0;
    }

    static bool IsDriveRoot(string path, DriveInfo d) =>
        string.Equals(path.TrimEnd('\\', '/'), d.RootDirectory.FullName.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static bool IsDiscMeta(string rel) =>
        rel.Equals("disc.json", StringComparison.OrdinalIgnoreCase) || rel.Equals(DiscFiles.ManifestName, StringComparison.OrdinalIgnoreCase) ||
        rel.Equals(DiscFiles.EncryptedManifestName, StringComparison.OrdinalIgnoreCase) ||
        rel.Equals(KeyFile.FileName, StringComparison.OrdinalIgnoreCase) || rel.Equals(DiscFiles.ReadmeName, StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith("_catalog/", StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith("System Volume Information/", StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith("$RECYCLE.BIN/", StringComparison.OrdinalIgnoreCase);
}
