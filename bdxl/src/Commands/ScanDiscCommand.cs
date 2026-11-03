namespace Bdxl;

public sealed class ScanDiscCommand : ICommand
{
    public string Name => "scandisc";
    public string Group => CommandGroup.ExistingDiscs;
    public string Summary => "Record a bdxl disc in the catalog from its own manifest";
    public string Usage => "scandisc <mountPath> [--date D] [--capacity S] [--cost P] [--update] [--dry-run]";
    public bool ReadOnlyFor(Options opt) => opt.Has("dry-run");
    public bool Interruptible => true;
    public string Details => """
        Reads a disc made by bdxl (or a folder laid out like one): its disc.json, its encrypted manifests (with the
        catalog's keys) and packs.sha256, and links its files to the catalog - files the catalog does not know get new
        entries. This is how a catalog is rebuilt from the discs ('init --key-from <disc>' first), or brought up to
        date after a rollback. Nothing is decrypted but the manifests; the copies count as 'unverified' until
        'verify' has checked the disc's packs.
          --date yyyy-MM-dd  when it was burned (default: the date in disc.json)
          --capacity 100GB   size of the disc (lets 'stage --append' add to it)      --cost 24.99   what it cost
          --update           the disc is already in the catalog: merge what is on it
          --dry-run          show what would happen, change nothing
        A disc of another catalog (another master key) needs that catalog, or one made with 'init --key-from <disc>'.
        Discs written without bdxl cannot be recorded.
        """;

    public int Run(Ctx c)
    {
        if (c.Args.Count != 1) throw new UserException("Usage: " + Usage);
        string mount = Path.GetFullPath(c.Args[0]);
        if (!Directory.Exists(mount)) throw new UserException($"Not a directory: {mount}");
        bool dry = c.Opt.Has("dry-run");

        // 1. which disc is this?
        DiscInfo info;
        try
        {
            info = JsonStore.ReadObject<DiscInfo>(Path.Combine(mount, DiscFiles.InfoName))
                   ?? throw new UserException($"{mount} is not a bdxl disc (no disc.json). Discs written without bdxl cannot be recorded.");
        }
        catch (JsonException ex) { throw new UserException($"disc.json cannot be read: {ex.Message}"); }
        if (info.Unreadable() is { } why) throw new UserException($"{info.Label} cannot be read: {why}.");
        if (info.KeyId != null && !c.Keys.Contains(info.KeyId))
            throw new UserException($"{info.Label} is encrypted with a key this catalog does not have (key id {info.KeyId}). " + DiscFiles.OtherCatalogHint(mount));
        string label = info.Label;

        var existing = c.Cat.Discs.FirstOrDefault(d => d.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !c.Opt.Has("update"))
            throw new UserException($"{existing.Label} is already in the catalog. Use --update to merge what is on this disc, or 'verify' to check it.");
        label = existing?.Label ?? label;
        Console.WriteLine($"Disc {label}{(existing != null ? ", updating its existing record" : "")}");
        DateTime burnedAt = c.Opt.GetDate("date") ?? existing?.BurnedUtc ?? info.CreatedUtc;

        // 2. what is on it: the manifests of every session, and the packs
        var manifestHashes = new Dictionary<int, string>();
        var lines = DiscFiles.ReadManifest(mount, c.Keys, manifestHashes) ?? throw new UserException($"{label} has no manifest.");
        var packList = DiscFiles.ReadPackList(mount);
        int packs = Math.Max(packList.Count == 0 ? 0 : packList.Keys.Max(), lines.Count == 0 ? 0 : lines.Max(l => DiscFormat.PackOf(l.At + Math.Max(0, l.Size - 1))));
        var problems = new List<string>();
        for (int p = 1; p <= packs; p++)
        {
            if (!File.Exists(Path.Combine(mount, DiscFormat.PackPath(p)))) problems.Add($"pack {p} is missing ({DiscFormat.PackPath(p)})");
            if (!packList.ContainsKey(p)) problems.Add($"packs.sha256 has no hash for pack {p}");
        }
        Console.WriteLine($"{lines.Count:N0} file(s) in {packs:N0} pack(s), {info.Sessions} session(s).");

        // 3. connect what is on the disc with what the catalog already knows
        var byPathIdx = c.Cat.Files.GroupBy(f => f.RootName + "/" + f.RelPath, StringComparer.Ordinal)
                                   .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var byHashIdx = c.Cat.Files.Where(f => f.Sha256.Length > 0).ToLookup(f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var createdHere = new HashSet<FileRecord>();   // a disc with appended sessions can hold several versions of one path
        int sameVersion = 0, olderVersion = 0, otherPath = 0, created = 0, already = 0;
        var links = new List<(FileRecord File, BackupRef Copy)>();

        foreach (var line in lines.OrderBy(l => l.At))
        {
            string sha = (line.IsPart ? line.FileSha256! : line.Sha256).ToUpperInvariant();
            long fileSize = line.IsPart ? line.FileSize!.Value : line.PlainSize;
            DateTime modified = line.Modified ?? burnedAt;
            var candidates = byPathIdx.TryGetValue(line.Source, out var known) ? known : [];
            var rec = candidates.FirstOrDefault(x => Hashing.SameHash(x.Sha256, sha));
            if (rec != null) sameVersion++;
            else if (candidates.Count > 0)                                           // same path, other content: an older/newer version
            {
                rec = candidates[0];
                olderVersion++;
                // only known from this disc: the later copy is the current version
                if (createdHere.Contains(rec) && modified > rec.ModifiedUtc) (rec.Sha256, rec.Size, rec.ModifiedUtc) = (sha, fileSize, modified);
            }
            else if ((rec = byHashIdx[sha].FirstOrDefault()) != null) otherPath++;  // same content under another path (moved/renamed)
            else   // not known at all: create an entry (the source file is not available)
            {
                int slash = line.Source.IndexOf('/');
                string rootName = slash > 0 ? line.Source[..slash] : "(imported)";
                string relPath = slash > 0 ? line.Source[(slash + 1)..] : line.Source;
                string root = c.Cat.Files.FirstOrDefault(f => f.RootName == rootName && !f.Root.StartsWith("imported:", StringComparison.Ordinal))?.Root
                              ?? "imported:" + label;
                rec = new FileRecord
                {
                    Root = root, RootName = rootName, RelPath = relPath,
                    Size = fileSize, ModifiedUtc = modified, Sha256 = sha, AddedUtc = DateTime.UtcNow, MissingSinceUtc = DateTime.UtcNow
                };
                if (!dry) c.Cat.Files.Add(rec);
                if (known == null) byPathIdx[line.Source] = [rec];
                else known.Add(rec);
                createdHere.Add(rec);
                created++;
            }

            var copy = new BackupRef
            {
                Disc = label, At = line.At, StoredSize = line.Size, Length = line.PlainSize, Sha256 = sha, BackedUpUtc = burnedAt,
                Unverified = true, Compression = line.Compression ?? 0, Codec = line.Codec
            };
            if (line.IsPart)
            {
                copy.Part = line.Part!.Value; copy.Parts = line.Parts!.Value; copy.Offset = line.Offset!.Value;
                copy.PartSha256 = line.Sha256.ToUpperInvariant();
            }
            if (rec.Backups.Any(b => b.Disc == label && b.At == line.At)) { already++; continue; }
            links.Add((rec, copy));
        }

        // 4. report
        Console.WriteLine($"  matched to known files: {sameVersion:N0} same version, {olderVersion:N0} other version of the same path, {otherPath:N0} same content under another path");
        Console.WriteLine($"  new catalog entries (not seen in any source scan): {created:N0}");
        if (already > 0) Console.WriteLine($"  already recorded for this disc: {already:N0}");
        foreach (var p in problems.Take(20)) Console.WriteLine("  PROBLEM " + p);
        Console.WriteLine($"  The copies count as unverified until 'verify {label} <mount>' has checked the disc.");

        if (dry) { Console.WriteLine("\nDry run - the catalog was not changed."); return 0; }
        if (Interrupt.Requested)
        {
            Console.WriteLine("\nStopped by Ctrl+C. Nothing was recorded; the catalog is unchanged.");
            return Interrupt.ExitCode;
        }

        foreach (var (file, copy) in links) file.Backups.Add(copy);
        var disc = existing ?? new DiscRecord { Label = label, State = "burned", CreatedUtc = info.CreatedUtc, BurnedUtc = burnedAt, Note = "read back with scandisc" };
        disc.KeyId = info.KeyId;
        disc.Sessions = Math.Max(1, info.Sessions);
        disc.Packs = Enumerable.Range(1, packs).Select(p => packList.GetValueOrDefault(p, "")).ToList();
        if (disc.Packs.Any(h => h.Length == 0)) disc.Packs = [];   // incomplete: 'verify --deep' still checks the disc
        disc.Manifests = Enumerable.Range(1, disc.Sessions).Select(s => manifestHashes.GetValueOrDefault(s, "")).ToList();
        var writtenBy = disc.WrittenBy;   // disc.json names the build of the latest session only
        disc.WrittenBy = Enumerable.Range(1, disc.Sessions)
            .Select(s => s == disc.Sessions && info.CreatedBy != null ? info.CreatedBy : s <= writtenBy.Count ? writtenBy[s - 1] : "").ToList();
        var onDisc = c.Arc.CopiesOn(label);
        disc.FileCount = onDisc.Count;
        disc.UsedBytes = onDisc.Sum(x => x.Copy.StoredSize);
        disc.OccupiedBytes = DiscSpace.Folder(mount) + disc.Sessions * (disc.Reserve ?? 0);
        if (c.Opt.Get("capacity") is { } capText) disc.Capacity = SizeText.Parse(capText);
        if (c.Opt.GetMoney("cost") is { } cost) disc.Cost = cost;
        if (existing == null) c.Cat.Discs.Add(disc);

        int linked = c.Arc.LinkDuplicates();   // other catalog files with identical content are now covered by this disc too
        if (linked > 0) Console.WriteLine($"  {linked:N0} more catalog file(s) have identical content and now count as backed up");
        c.Cat.Save();
        Console.WriteLine($"\n{label}: {links.Count:N0} file(s) recorded.");
        return problems.Count > 0 ? 3 : 0;
    }
}
