// Program.cs  (.NET 8+)
// BDXL archive planner + catalog. No compression, no archiving: files are copied as-is.
//
// Catalog (human readable, one JSON object per line, easy to diff / grep / edit):
//   <catalog>/files.jsonl   every file ever seen: path, size, mtime, SHA-256, and where/when it was backed up
//   <catalog>/discs.jsonl   every disc: label, state (staged/burned), dates, size
//
// Commands:
//   scan   <sourceDir> [--name alias]        index files, hash new/changed ones
//   plan                                     show which pending files would go on which discs
//   stage  <destDir> [--max-discs N] [--min-fill 0.9]
//                                            copy next N discs worth of files into <destDir>/BDXL-xxxx (+ manifest.jsonl)
//   burned <label> [--date yyyy-MM-dd]       mark a staged disc as burned (records backup date)
//   verify <label> <mountedDiscPath>         re-hash everything on a disc against the catalog
//   discard <label>                          forget a staged (not yet burned) disc
//   find   <text|hashPrefix>                 where is this file / which discs hold it
//   status                                   summary
//   discs                                    list discs and their state
//   lost|destroyed <label> [--reason text]   disc gone: its copies stop counting, files become pending again
//   found <label>                            a lost disc turned up
//   restage <label> <destDir> [--from a;b] [--allow-partial]
//                                            rebuild the same disc folder (same label/files) to burn again
//   restore <destDir> <query|*> --mount F:\;G:\ [--flat] [--overwrite] [--source name]
//                                            copy files back from mounted discs into <destDir>
//   catbackup <destDir> [--parity-percent 25]  catalog snapshot + Reed-Solomon repair data
//   catverify <backupDir> [--repair]         check / repair a catalog backup
//   catrestore <backupDir> [--force]         restore the catalog from a backup
//   (stage also takes --with-catalog: writes a catalog backup into <disc>/_catalog)
//
// Dedup: files are identified by SHA-256. Content already on any disc (even if the file was moved/renamed/copied,
// or the original was deleted) is never stored twice; such files get a backup entry with "alias": true.
//
// Common options: --catalog <dir> (default ./catalog)   --reserve <bytes> (per-disc safety margin)

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length == 0) return Usage();

string cmd = args[0].ToLowerInvariant();
var pos = new List<string>();
var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var flagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "flat", "overwrite", "repair", "force", "allow-partial", "with-catalog", "update", "dry-run", "write-disc-json" };   // options without a value
for (int i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--"))
    {
        string key = args[i][2..];
        opt[key] = !flagNames.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
    }
    else
        pos.Add(args[i]);
}

string catalogDir = Path.GetFullPath(opt.GetValueOrDefault("catalog", "catalog"));
if (opt.ContainsKey("reserve") && cmd != "blanks")
{
    Console.Error.WriteLine("--reserve now belongs to blank discs: use 'blanks add <count> --reserve <size>' or 'blanks reserve <size>'.");
    return 1;
}

var cat = Catalog.Load(catalogDir);

try
{
    return cmd switch
    {
        "scan" when pos.Count == 1 => Scan(pos[0]),
        "plan" => Plan(),
        "stage" when pos.Count == 1 => Stage(pos[0]),
        "burned" when pos.Count == 1 => Burned(pos[0]),
        "verify" when pos.Count == 2 => Verify(pos[0], pos[1]),
        "discard" when pos.Count == 1 => Discard(pos[0]),
        "find" when pos.Count == 1 => Find(pos[0]),
        "status" => Status(),
        "blanks" => Blanks(pos),
        "scandisc" when pos.Count == 1 => ScanDisc(pos[0]),
        "discjson" when pos.Count == 2 => DiscJson(pos[0], pos[1]),
        "discs" => Discs(),
        "lost" when pos.Count == 1 => MarkDead(pos[0], "lost"),
        "destroyed" when pos.Count == 1 => MarkDead(pos[0], "destroyed"),
        "found" when pos.Count == 1 => Found(pos[0]),
        "restage" when pos.Count == 2 => Restage(pos[0], pos[1]),
        "restore" when pos.Count == 2 => Restore(pos[0], pos[1]),
        "catbackup" when pos.Count == 1 => CatBackup(pos[0]),
        "catverify" when pos.Count == 1 => CatVerify(pos[0]),
        "catrestore" when pos.Count == 1 => CatRestore(pos[0]),
        _ => Usage()
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

// ---------------------------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------------------------

int Scan(string rootArg)
{
    string root = Path.GetFullPath(rootArg);
    if (root.Length > Path.GetPathRoot(root)!.Length)
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (!Directory.Exists(root)) throw new InvalidOperationException($"Not a directory: {root}");

    string name = opt.GetValueOrDefault("name") ?? Path.GetFileName(root);
    if (string.IsNullOrEmpty(name))
        throw new InvalidOperationException("Cannot derive a name from this path; use --name <alias>.");
    if (cat.Files.Any(f => f.RootName.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Root != root))
        throw new InvalidOperationException($"A different source is already catalogued as '{name}'. Use --name <alias>.");

    var byPath = cat.Files.Where(f => f.Root == root).ToDictionary(f => f.RelPath, StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    int added = 0, changed = 0, unchanged = 0, skipped = 0;
    var now = DateTime.UtcNow;
    var lastSave = DateTime.UtcNow;

    var enumOpts = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint   // skip symlinks / junctions
    };

    foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", enumOpts))
    {
        string rel = Path.GetRelativePath(root, fi.FullName).Replace('\\', '/');
        seen.Add(rel);
        long size = fi.Length;
        DateTime mtime = fi.LastWriteTimeUtc;

        byPath.TryGetValue(rel, out var rec);
        if (rec != null && rec.Size == size && rec.ModifiedUtc == mtime)
        {
            rec.MissingSinceUtc = null;
            unchanged++;
            continue;
        }

        string hash;
        try
        {
            Console.WriteLine($"hashing {rel} ({size:N0} bytes)");
            hash = Util.HashFile(fi.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  skipped: {ex.Message}");
            skipped++;
            continue;
        }

        if (rec == null)
        {
            rec = new FileRecord { Root = root, RootName = name, RelPath = rel, AddedUtc = now };
            cat.Files.Add(rec);
            byPath[rel] = rec;
            added++;
        }
        else if (rec.Sha256 != hash) changed++;
        else unchanged++;

        rec.Size = size;
        rec.ModifiedUtc = mtime;
        rec.Sha256 = hash;
        rec.MissingSinceUtc = null;

        if (DateTime.UtcNow - lastSave > TimeSpan.FromMinutes(2))   // don't lose hours of hashing on a crash
        {
            cat.Save();
            lastSave = DateTime.UtcNow;
        }
    }

    int missing = 0;
    foreach (var f in cat.Files.Where(f => f.Root == root && f.MissingSinceUtc == null && !seen.Contains(f.RelPath)))
    {
        f.MissingSinceUtc = now;
        missing++;
    }

    int linked = LinkDuplicates();
    cat.Save();
    Console.WriteLine($"\nScan done: {added} new, {changed} changed, {unchanged} unchanged, " +
                      $"{missing} no longer on source, {skipped} skipped (unreadable).");
    if (linked > 0) Console.WriteLine($"{linked} file(s) already backed up under another path (same hash) - linked, nothing to burn.");
    return 0;
}

// Allocates pending files to the registered blank discs (see 'blanks') and reports whether everything fits.
// Changes nothing; 'stage' uses exactly the same allocation.
int Plan()
{
    var plan = MakePlan();
    long pendingBytes = plan.Pending.Sum(f => f.Size);
    Console.WriteLine($"Pending: {plan.Pending.Count:N0} file(s), {pendingBytes:N0} bytes");

    if (cat.Blanks.Count == 0)
    {
        if (plan.Pending.Count > 0)
            Console.WriteLine("No blank discs are registered, so nothing can be allocated. Register some, e.g.:\n" +
                              "  blanks add 5 --size 96882130944 --reserve 200000000");
        return plan.Pending.Count > 0 ? 2 : 0;
    }

    Console.WriteLine($"Blank discs: {cat.Blanks.Count} ({cat.Blanks.Sum(b => b.Usable):N0} usable bytes)\n");
    foreach (var bin in plan.Bins.Where(b => b.Files.Count > 0))
        Console.WriteLine($"  {bin.Blank.Id}  {bin.Blank.Capacity,18:N0} B disc: {bin.Files.Count,8:N0} files, {bin.Used,18:N0} bytes " +
                          $"({100.0 * bin.Used / bin.Usable:F1}% of usable)");
    int unused = plan.Bins.Count(b => b.Files.Count == 0);
    if (unused > 0) Console.WriteLine($"  {unused} blank disc(s) would stay unused.");

    if (plan.Unplaced.Count == 0)
    {
        Console.WriteLine("\nAll pending data fits on the designated blank discs.");
        return 0;
    }

    long left = plan.Unplaced.Sum(f => f.Size);
    long biggest = cat.Blanks.Max(b => b.Usable);
    Console.WriteLine($"\nNOT ALL DATA FITS: {plan.Unplaced.Count:N0} file(s), {left:N0} bytes cannot be placed on the {cat.Blanks.Count} designated blank disc(s).");
    Console.WriteLine($"Roughly {(left + biggest - 1) / biggest} more disc(s) of the largest size would be needed.");
    PrintTooBig(plan.TooBig);
    var rest = plan.Unplaced.Except(plan.TooBig).ToList();
    if (rest.Count > 0)
    {
        Console.WriteLine("Left over (do not fit in the remaining space):");
        foreach (var f in rest.OrderByDescending(f => f.Size).Take(20)) Console.WriteLine($"  {f.RootName}/{f.RelPath}  ({f.Size:N0} bytes)");
        if (rest.Count > 20) Console.WriteLine($"  ... and {rest.Count - 20:N0} more");
    }
    return 2;
}

int Stage(string destArg)
{
    string destDir = Path.GetFullPath(destArg);
    int maxDiscs = int.Parse(opt.GetValueOrDefault("max-discs", "1"), CultureInfo.InvariantCulture);
    double minFill = double.Parse(opt.GetValueOrDefault("min-fill", "0"), CultureInfo.InvariantCulture);

    var plan = MakePlan();
    if (cat.Blanks.Count == 0)
    {
        Console.WriteLine("No blank discs are registered. Register some first, e.g.:  blanks add 5 --size 96882130944 --reserve 200000000");
        return 1;
    }
    PrintTooBig(plan.TooBig);

    var chosen = plan.Bins.Where(b => b.Files.Count > 0 && b.Used >= minFill * b.Usable)
        .OrderByDescending(b => (double)b.Used / b.Usable).Take(maxDiscs).ToList();
    if (chosen.Count == 0) { Console.WriteLine("Nothing to stage (no pending files, or no blank disc would be full enough)."); return 0; }
    if (plan.Unplaced.Count > 0)
        Console.WriteLine($"Note: {plan.Unplaced.Count:N0} pending file(s) do not fit on the registered blank discs (see 'plan').\n");

    foreach (var bin in chosen)
    {
        string label = NextLabel();
        string dir = Path.Combine(destDir, label);
        if (Directory.Exists(dir)) throw new InvalidOperationException($"Folder already exists: {dir}");

        var disc = new DiscRecord
        {
            Label = label,
            State = "staged",
            CreatedUtc = DateTime.UtcNow,
            Capacity = bin.Blank.Capacity,
            Reserve = bin.Blank.Reserve
        };
        cat.Discs.Add(disc);
        var manifest = new List<ManifestLine>();

        foreach (var f in bin.Files)
        {
            string discPath = $"{f.RootName}/{f.RelPath}";
            string src = Path.Combine(f.Root, f.RelPath);
            string dst = Path.Combine(dir, discPath);
            Console.WriteLine($"[{label}] {discPath}");

            string got;
            try
            {
                got = Util.CopyAndHash(src, dst);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"  FAILED: {ex.Message}");
                if (File.Exists(dst)) File.Delete(dst);
                continue;
            }

            if (!got.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(dst);
                Console.WriteLine("  changed since last scan - not staged. Re-run 'scan'.");
                continue;
            }

            File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
            f.Backups.Add(new BackupRef { Disc = label, DiscPath = discPath, Sha256 = got });
            disc.FileCount++;
            disc.UsedBytes += f.Size;
            manifest.Add(new ManifestLine(discPath, f.Size, got));
        }

        if (disc.FileCount == 0)
        {
            cat.Discs.Remove(disc);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Console.WriteLine($"{label}: nothing could be staged.");
            continue;
        }

        cat.Blanks.Remove(bin.Blank);          // this blank disc is now spoken for
        WriteDiscFiles(dir, disc, manifest);   // disc describes itself (manifest.jsonl + disc.json)
        int dups = LinkDuplicates();   // other files with identical content now point at this disc too
        cat.Save();
        if (opt.ContainsKey("with-catalog"))   // remember to leave room for it in the blanks' reserve
        {
            Console.WriteLine("Writing catalog backup into the disc folder:");
            CatBackupTo(Path.Combine(dir, "_catalog"));
        }
        Console.WriteLine($"{label}: {disc.FileCount:N0} files, {disc.UsedBytes:N0} bytes staged in {dir}" +
                          (dups > 0 ? $" ({dups} duplicate file(s) linked, not stored again)" : ""));
    }

    Console.WriteLine("\nBurn each staged folder, then run:  burned <label>   (and optionally: verify <label> <mountPath>)");
    return 0;
}

int Burned(string label)
{
    var disc = FindDisc(label);
    if (disc.State is "lost" or "destroyed")
        throw new InvalidOperationException($"{disc.Label} is {disc.State}. Use 'restage' to rebuild it first.");
    if (disc.State == "burned") { Console.WriteLine($"{disc.Label} is already marked as burned."); return 1; }

    DateTime when = opt.TryGetValue("date", out var s) ? Util.ParseDate(s) : DateTime.UtcNow;
    disc.State = "burned";
    disc.BurnedUtc = when;
    foreach (var b in cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label))
        b.BackedUpUtc = when;

    cat.Save();
    Console.WriteLine($"{disc.Label} marked as burned on {when:yyyy-MM-dd}.");
    return 0;
}

int Discard(string label)
{
    var disc = FindDisc(label);
    if (disc.State != "staged") throw new InvalidOperationException("Only staged (not burned) discs can be discarded.");
    foreach (var f in cat.Files) f.Backups.RemoveAll(b => b.Disc == disc.Label);
    cat.Discs.Remove(disc);
    bool returned = disc.Capacity is not null;
    if (disc.Capacity is { } cap)   // the blank disc it was going to use is available again
        cat.Blanks.Add(new BlankDisc { Id = NextBlankId(), Capacity = cap, Reserve = disc.Reserve ?? 0, AddedUtc = DateTime.UtcNow });
    cat.Save();
    Console.WriteLine($"{disc.Label} discarded; its files are pending again" + (returned ? " and its blank disc is back in the inventory" : "") +
                      ". (The staged folder, if any, was not deleted.)");
    return 0;
}

int Verify(string label, string mountArg)
{
    var disc = FindDisc(label);
    string mount = Path.GetFullPath(mountArg);
    int ok = 0, bad = 0, miss = 0;

    var items = cat.Files.SelectMany(rec => rec.Backups.Where(bk => bk.Disc == disc.Label))
        .GroupBy(bk => bk.DiscPath, StringComparer.Ordinal).Select(g => g.First()).ToList();   // aliases share one stored copy
    foreach (var bk in items)
    {
        string p = Path.Combine(mount, bk.DiscPath);
        if (!File.Exists(p)) { Console.WriteLine($"MISSING  {bk.DiscPath}"); miss++; continue; }
        if (Util.HashFile(p).Equals(bk.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            ok++;
            foreach (var same in cat.Files.SelectMany(f => f.Backups).Where(x => x.Disc == disc.Label && x.DiscPath == bk.DiscPath))
                same.Unverified = false;   // now really checked (matters for discs imported with 'scandisc')
        }
        else { Console.WriteLine($"CORRUPT  {bk.DiscPath}"); bad++; }
    }
    if (ok > 0) cat.Save();

    Console.WriteLine($"\n{disc.Label}: {ok} OK, {bad} corrupt, {miss} missing (of {items.Count}).");
    return bad + miss == 0 ? 0 : 3;
}

int Find(string text)
{
    var hits = cat.Files.Where(f => Matches(f, text))
        .OrderBy(f => f.RootName, StringComparer.Ordinal).ThenBy(f => f.RelPath, StringComparer.Ordinal)
        .ToList();

    foreach (var f in hits)
    {
        Console.WriteLine($"{f.RootName}/{f.RelPath}  {f.Size:N0} bytes  sha256 {f.Sha256[..16]}...  " +
                          (f.MissingSinceUtc != null ? $"[gone from source since {f.MissingSinceUtc:yyyy-MM-dd}]" : ""));
        if (f.Backups.Count == 0) Console.WriteLine("    not backed up");
        foreach (var b in f.Backups)
        {
            var d = cat.Discs.FirstOrDefault(x => x.Label == b.Disc);
            string when = b.BackedUpUtc is { } t ? t.ToString("yyyy-MM-dd") : "not burned yet";
            string ver = b.Sha256 == f.Sha256 ? "current version" : "OLDER version";
            string via = b.Alias ? $" - same content stored as {b.DiscPath}" : "";
            string unv = b.Unverified ? " [taken from manifest/catalog, not re-hashed - run 'verify']" : "";
            Console.WriteLine($"    {b.Disc} ({d?.State ?? "?"}, {when}) {ver}{via}{unv}");
        }
    }
    Console.WriteLine($"\n{hits.Count} match(es).");
    return hits.Count > 0 ? 0 : 1;
}

int Status()
{
    Console.WriteLine($"Catalog: {catalogDir}");
    foreach (var g in cat.Files.GroupBy(f => f.RootName))
        Console.WriteLine($"  source '{g.Key}': {g.Count():N0} files, {g.Sum(f => f.Size):N0} bytes");

    int onSource = cat.Files.Count(f => f.MissingSinceUtc == null);
    var deadDiscs = DeadDiscs();
    int safe = cat.Files.Count(f => f.MissingSinceUtc == null && f.Backups.Any(b => !deadDiscs.Contains(b.Disc) && b.Sha256 == f.Sha256 && b.BackedUpUtc != null));
    int dataLost = cat.Files.Count(f => f.MissingSinceUtc != null && f.Backups.Count > 0 && !f.Backups.Any(b => !deadDiscs.Contains(b.Disc)));
    var plan = MakePlan();

    Console.WriteLine($"Files still on source: {onSource:N0}; current version burned: {safe:N0}");
    Console.WriteLine($"Pending: {plan.Pending.Count:N0} files, {plan.Pending.Sum(f => f.Size):N0} bytes");
    Console.WriteLine($"Blank discs registered: {cat.Blanks.Count}" +
                      (plan.Pending.Count == 0 ? "" :
                       plan.Unplaced.Count == 0 ? " - all pending data fits." :
                       $" - {plan.Unplaced.Count:N0} pending file(s) do NOT fit (run 'plan')."));
    Console.WriteLine($"Discs: {cat.Discs.Count(d => d.State == "burned")} burned, {cat.Discs.Count(d => d.State == "staged")} staged (not yet burned), " +
                      $"{cat.Discs.Count(d => d.State == "lost")} lost, {cat.Discs.Count(d => d.State == "destroyed")} destroyed");
    if (dataLost > 0) Console.WriteLine($"WARNING: {dataLost:N0} file(s) are gone from the source and have no surviving copy on any disc.");
    PrintTooBig(plan.TooBig);
    return 0;
}

// ---------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------

HashSet<string> DeadDiscs() =>
    cat.Discs.Where(d => d.State is "lost" or "destroyed").Select(d => d.Label).ToHashSet();

bool Matches(FileRecord f, string text) =>
    text == "*" ||
    f.RelPath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
    f.Sha256.StartsWith(text, StringComparison.OrdinalIgnoreCase) ||
    f.Backups.Any(b => b.Sha256.StartsWith(text, StringComparison.OrdinalIgnoreCase));

void WriteDiscFiles(string dir, DiscRecord disc, List<ManifestLine> manifest)
{
    Util.WriteJsonl(Path.Combine(dir, "manifest.jsonl"), manifest);
    Util.WriteJson(Path.Combine(dir, "disc.json"), new DiscInfo(disc.Label, disc.CreatedUtc, disc.FileCount, disc.UsedBytes));
}

int Discs()
{
    foreach (var d in cat.Discs)
        Console.WriteLine($"{d.Label}  {d.State,-9}  {d.FileCount,9:N0} files  {d.UsedBytes,18:N0} bytes  created {d.CreatedUtc:yyyy-MM-dd}" +
                          (d.BurnedUtc is { } burnedAt ? $"  burned {burnedAt:yyyy-MM-dd}" : "") +
                          (d.LostUtc is { } lostAt ? $"  {d.State} {lostAt:yyyy-MM-dd}" : "") +
                          (d.RestagedUtc is { } restagedAt ? $"  restaged {restagedAt:yyyy-MM-dd}" : "") +
                          (string.IsNullOrEmpty(d.Note) ? "" : $"  # {d.Note}"));
    return 0;
}

// ---- lost / destroyed / found -------------------------------------------------------------

int MarkDead(string label, string state)
{
    var disc = FindDisc(label);
    if (disc.State == state) { Console.WriteLine($"{disc.Label} is already marked {state}."); return 1; }
    if (disc.State == "destroyed") throw new InvalidOperationException($"{disc.Label} is destroyed; that cannot be changed.");

    disc.State = state;
    disc.LostUtc = DateTime.UtcNow;
    if (opt.TryGetValue("reason", out var why)) disc.Note = why;

    var affected = cat.Files.Where(f => f.Backups.Any(b => b.Disc == disc.Label)).ToList();
    int relinked = LinkDuplicates();   // files whose content also exists on another live disc keep a valid copy
    var dead = DeadDiscs();
    var exposed = affected.Where(f => !f.Backups.Any(b => !dead.Contains(b.Disc) && b.Sha256 == f.Sha256)).ToList();
    var rebackup = exposed.Where(f => f.MissingSinceUtc == null).ToList();
    var gone = exposed.Where(f => f.MissingSinceUtc != null).ToList();
    cat.Save();

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

int Found(string label)
{
    var disc = FindDisc(label);
    if (disc.State != "lost") throw new InvalidOperationException("Only discs marked 'lost' can be marked as found.");
    disc.State = disc.BurnedUtc != null ? "burned" : "staged";
    disc.LostUtc = null;
    cat.Save();
    Console.WriteLine($"{disc.Label} is '{disc.State}' again. Check it with:  verify {disc.Label} <mountPath>");
    return 0;
}

// ---- restage ------------------------------------------------------------------------------

// Rebuilds the folder for an existing disc (same label, same paths, same content) so it can be burned again.
// Data comes from the current source files if their hash still matches, otherwise from --from folders
// (e.g. another mounted copy, a partly readable disc, a recovery folder laid out like the disc).
int Restage(string label, string destArg)
{
    var disc = FindDisc(label);
    string dir = Path.Combine(Path.GetFullPath(destArg), disc.Label);
    if (Directory.Exists(dir)) throw new InvalidOperationException($"Folder already exists: {dir}");
    var fromDirs = (opt.GetValueOrDefault("from") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    bool allowPartial = opt.ContainsKey("allow-partial");

    var entries = cat.Files.SelectMany(f => f.Backups.Where(b => b.Disc == disc.Label))
        .GroupBy(b => b.DiscPath, StringComparer.Ordinal).Select(g => g.First()).ToList();
    if (entries.Count == 0) throw new InvalidOperationException($"The catalog has no files for {disc.Label}.");
    var onSource = cat.Files.Where(f => f.MissingSinceUtc == null).ToLookup(f => f.Sha256, StringComparer.OrdinalIgnoreCase);

    var manifest = new List<ManifestLine>();
    var missing = new List<BackupRef>();

    foreach (var entry in entries)
    {
        string dst = Path.Combine(dir, entry.DiscPath);
        var candidates = onSource[entry.Sha256].Select(f => Path.Combine(f.Root, f.RelPath))
            .Concat(fromDirs.Select(fd => Path.Combine(fd, entry.DiscPath)))
            .Where(File.Exists).ToList();

        bool restored = false;
        foreach (var src in candidates)
        {
            try
            {
                string got = Util.CopyAndHash(src, dst);
                if (got.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                    restored = true;
                    break;
                }
                File.Delete(dst);   // wrong content (changed / damaged) - try the next candidate
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"  {src}: {ex.Message}");
                if (File.Exists(dst)) File.Delete(dst);
            }
        }

        if (restored)
        {
            Console.WriteLine($"[{disc.Label}] {entry.DiscPath}");
            manifest.Add(new ManifestLine(entry.DiscPath, new FileInfo(dst).Length, entry.Sha256));
        }
        else missing.Add(entry);
    }

    if (missing.Count > 0)
    {
        Console.WriteLine($"\n{missing.Count:N0} file(s) could not be rebuilt (no source file with identical content, none found under --from):");
        foreach (var m in missing.Take(50)) Console.WriteLine("  " + m.DiscPath);
        if (!allowPartial)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Console.WriteLine("\nNothing staged and the catalog is unchanged. Add --from <folder;folder> to point at other copies, " +
                              "or --allow-partial to restage without those files.");
            return 3;
        }
        var gonePaths = missing.Select(m => m.DiscPath).ToHashSet(StringComparer.Ordinal);
        foreach (var f in cat.Files) f.Backups.RemoveAll(b => b.Disc == disc.Label && gonePaths.Contains(b.DiscPath));
    }

    if (manifest.Count == 0)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        throw new InvalidOperationException("No file could be rebuilt; the catalog is unchanged.");
    }

    disc.State = "staged";           // it has to be burned again
    disc.BurnedUtc = null;
    disc.LostUtc = null;
    disc.RestagedUtc = DateTime.UtcNow;
    disc.FileCount = manifest.Count;
    disc.UsedBytes = manifest.Sum(x => x.Size);
    foreach (var b in cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label)) b.BackedUpUtc = null;

    WriteDiscFiles(dir, disc, manifest);
    LinkDuplicates();
    cat.Save();
    Console.WriteLine($"\n{disc.Label}: {manifest.Count:N0} files, {disc.UsedBytes:N0} bytes rebuilt in {dir}");
    Console.WriteLine($"Burn it, then run:  burned {disc.Label}");
    return 0;
}

// ---- restore ------------------------------------------------------------------------------

// Copies files back from mounted discs. Layout under <destDir>: <source name>/<relative path> (or --flat).
int Restore(string destArg, string query)
{
    string dest = Path.GetFullPath(destArg);
    string destPrefix = dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    bool flat = opt.ContainsKey("flat"), overwrite = opt.ContainsKey("overwrite");

    // which disc is mounted where (identified by disc.json written at stage time)
    var mounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var mp in (opt.GetValueOrDefault("mount") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string info = Path.Combine(mp, "disc.json");
        string? lbl = File.Exists(info) ? Util.ReadJson<DiscInfo>(info)?.Label : opt.GetValueOrDefault("disc");
        if (lbl == null) { Console.WriteLine($"Ignoring {mp}: no disc.json there (for a single mount you can pass --disc <label>)."); continue; }
        mounts[lbl] = mp;
    }

    var dead = DeadDiscs();
    var wanted = cat.Files.Where(f => Matches(f, query))
        .Where(f => !opt.TryGetValue("source", out var srcName) || f.RootName.Equals(srcName, StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f.RootName, StringComparer.Ordinal).ThenBy(f => f.RelPath, StringComparer.Ordinal)
        .ToList();
    if (wanted.Count == 0) { Console.WriteLine("No file in the catalog matches."); return 1; }

    // pick the copy to restore: live disc, current version first, then the newest entry
    var jobs = new List<(FileRecord File, BackupRef Copy)>();
    var noCopy = new List<FileRecord>();
    foreach (var f in wanted)
    {
        var best = f.Backups.Where(x => !dead.Contains(x.Disc)).Reverse()
            .OrderByDescending(x => x.Sha256 == f.Sha256).ThenByDescending(x => x.BackedUpUtc != null)
            .FirstOrDefault();
        if (best == null) noCopy.Add(f); else jobs.Add((f, best));
    }

    int done = 0, failed = 0, skipped = 0;
    var needDiscs = new SortedDictionary<string, int>(StringComparer.Ordinal);

    foreach (var job in jobs.OrderBy(j => j.Copy.Disc, StringComparer.Ordinal).ThenBy(j => j.Copy.DiscPath, StringComparer.Ordinal))
    {
        var f = job.File;
        var copy = job.Copy;
        if (!mounts.TryGetValue(copy.Disc, out var root))
        {
            needDiscs[copy.Disc] = needDiscs.GetValueOrDefault(copy.Disc) + 1;
            continue;
        }

        string rel = flat ? Path.GetFileName(f.RelPath) : f.RootName + "/" + f.RelPath;
        string target = Path.GetFullPath(Path.Combine(dest, rel));
        if (!target.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"SKIPPED (path escapes destination)  {rel}");
            failed++;
            continue;
        }
        if (File.Exists(target) && !overwrite) { Console.WriteLine($"EXISTS   {rel}  (use --overwrite)"); skipped++; continue; }

        string src = Path.Combine(root, copy.DiscPath);
        if (!File.Exists(src)) { Console.WriteLine($"MISSING  {copy.Disc}: {copy.DiscPath}"); failed++; continue; }

        string part = target + ".part";
        try
        {
            if (File.Exists(part)) File.Delete(part);
            string got = Util.CopyAndHash(src, part);
            if (!got.Equals(copy.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                Console.WriteLine($"CORRUPT  {copy.Disc}: {copy.DiscPath}  (hash mismatch, not restored)");
                failed++;
                continue;
            }
            File.SetLastWriteTimeUtc(part, File.GetLastWriteTimeUtc(src));
            File.Move(part, target, overwrite: true);
            done++;
            Console.WriteLine($"restored {rel}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"FAILED   {rel}: {ex.Message}");
            if (File.Exists(part)) File.Delete(part);
            failed++;
        }
    }

    Console.WriteLine($"\n{done:N0} restored, {skipped:N0} skipped (already exist), {failed:N0} failed.");
    if (needDiscs.Count > 0)
    {
        Console.WriteLine("Insert / mount these discs and run the same command again with --mount:");
        foreach (var kv in needDiscs)
            Console.WriteLine($"  {kv.Key}  ({kv.Value:N0} file(s))");
    }
    if (noCopy.Count > 0)
    {
        Console.WriteLine($"\n{noCopy.Count:N0} file(s) have no surviving copy on any disc:");
        foreach (var f in noCopy.Take(50)) Console.WriteLine($"  {f.RootName}/{f.RelPath}");
    }
    return failed > 0 ? 3 : needDiscs.Count > 0 || noCopy.Count > 0 ? 2 : 0;
}

// ---- catalog backup with repair data ------------------------------------------------------

int CatBackup(string destArg)
{
    cat.Save();
    string dir = Path.Combine(Path.GetFullPath(destArg), $"catalog-{DateTime.Now:yyyyMMdd-HHmmss}");
    Console.WriteLine($"Catalog backup -> {dir}");
    CatBackupTo(dir);
    Console.WriteLine("Keep it on a different medium than the catalog (or burn it onto the next disc: stage --with-catalog).");
    return 0;
}

void CatBackupTo(string dir)
{
    int pct = int.Parse(opt.GetValueOrDefault("parity-percent", "25"), CultureInfo.InvariantCulture);
    if (pct < 1 || pct > 50) throw new InvalidOperationException("--parity-percent must be between 1 and 50.");
    Directory.CreateDirectory(dir);

    foreach (string name in Util.CatalogFiles)
    {
        string src = Path.Combine(catalogDir, name);
        if (!File.Exists(src)) continue;
        byte[] data = File.ReadAllBytes(src);
        var meta = Rs.Protect(name, data, pct, out var parity);
        File.WriteAllBytes(Path.Combine(dir, name), data);
        File.WriteAllBytes(Path.Combine(dir, name + ".par"), parity);
        Util.WriteJson(Path.Combine(dir, name + ".repair.json"), meta);
        Console.WriteLine($"  {name}: {data.Length:N0} bytes + {parity.Length:N0} bytes repair data " +
                          $"(can rebuild up to {meta.ParityBlocks} damaged blocks of {meta.BlockSize:N0} bytes)");
    }

    File.WriteAllText(Path.Combine(dir, "README.txt"),
        $"BDXL catalog backup, created {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.\n\n" +
        "files.jsonl, discs.jsonl   the catalog (JSON lines, readable in any text editor)\n" +
        "*.repair.json, *.par       repair data: per-block SHA-256 + Reed-Solomon parity blocks (GF(256), Cauchy matrix)\n\n" +
        "Check:    bdxl catverify <this folder>\n" +
        "Repair:   bdxl catverify <this folder> --repair\n" +
        "Restore:  bdxl catrestore <this folder> --catalog <catalog folder> [--force]\n");
}

(RsStatus Status, byte[] Data, byte[]? Parity, int BadData, int BadParity) LoadCatFile(string dir, string name)
{
    var meta = Util.ReadJson<RepairMeta>(Path.Combine(dir, name + ".repair.json"))
               ?? throw new InvalidOperationException($"Unreadable repair file for {name}.");
    string dataPath = Path.Combine(dir, name), parPath = Path.Combine(dir, name + ".par");
    byte[]? data = File.Exists(dataPath) ? File.ReadAllBytes(dataPath) : null;
    byte[]? par = File.Exists(parPath) ? File.ReadAllBytes(parPath) : null;
    return Rs.Check(meta, data, par);
}

static string[] RepairNames(string dir)
{
    const string suffix = ".repair.json";
    var names = Directory.GetFiles(dir, "*" + suffix).Select(p => Path.GetFileName(p)[..^suffix.Length]).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    if (names.Length == 0) throw new InvalidOperationException($"No *.repair.json files in {dir}.");
    return names;
}

int CatVerify(string dirArg)
{
    string dir = Path.GetFullPath(dirArg);
    bool repair = opt.ContainsKey("repair");
    int damaged = 0, lostForever = 0;

    foreach (string name in RepairNames(dir))
    {
        var r = LoadCatFile(dir, name);
        if (r.Status == RsStatus.Ok) { Console.WriteLine($"{name}: OK"); continue; }
        if (r.Status == RsStatus.Unrecoverable)
        {
            Console.WriteLine($"{name}: UNRECOVERABLE ({r.BadData} damaged data block(s), {r.BadParity} damaged repair block(s) - more than the repair data can fix)");
            lostForever++;
            continue;
        }

        Console.WriteLine($"{name}: damaged but repairable ({r.BadData} data block(s), {r.BadParity} repair block(s))");
        if (repair)
        {
            File.WriteAllBytes(Path.Combine(dir, name), r.Data);
            if (r.Parity != null) File.WriteAllBytes(Path.Combine(dir, name + ".par"), r.Parity);
            Console.WriteLine("  repaired in place.");
        }
        else damaged++;
    }

    if (damaged > 0) Console.WriteLine("\nRun again with --repair to fix it in place.");
    return lostForever > 0 ? 3 : damaged > 0 ? 1 : 0;
}

int CatRestore(string dirArg)
{
    string dir = Path.GetFullPath(dirArg);
    bool force = opt.ContainsKey("force");
    var loaded = new List<(string Name, byte[] Data, RsStatus Status)>();

    foreach (string name in RepairNames(dir))
    {
        var r = LoadCatFile(dir, name);
        if (r.Status == RsStatus.Unrecoverable) throw new InvalidOperationException($"{name} is damaged beyond repair; nothing was restored.");
        loaded.Add((name, r.Data, r.Status));
    }
    foreach (var item in loaded)
        if (File.Exists(Path.Combine(catalogDir, item.Name)) && !force)
            throw new InvalidOperationException($"{Path.Combine(catalogDir, item.Name)} already exists; use --force to overwrite.");

    Directory.CreateDirectory(catalogDir);
    foreach (var item in loaded)
    {
        File.WriteAllBytes(Path.Combine(catalogDir, item.Name), item.Data);
        Console.WriteLine($"restored {item.Name}" + (item.Status == RsStatus.Repaired ? "  (repaired from parity data)" : ""));
    }
    Console.WriteLine($"Catalog restored into {catalogDir}");
    return 0;
}

// ---- blank discs --------------------------------------------------------------------------

//   blanks [list]
//   blanks add <count> [--size 96882130944|100GB|90GiB] [--reserve 200MB]
//   blanks reserve <size> [--size <only discs of this size>]   change the reserve of known blank discs
//   blanks remove <id>|all [--size <only discs of this size>]
int Blanks(List<string> a)
{
    string sub = a.Count > 0 ? a[0].ToLowerInvariant() : "list";
    long? sizeFilter = sub != "add" && opt.TryGetValue("size", out var sizeText) ? Util.ParseSize(sizeText) : null;

    if (sub == "list" && a.Count <= 1) return BlanksList();

    if (sub == "add" && a.Count == 2)
    {
        if (!int.TryParse(a[1], out int count) || count < 1)
            throw new InvalidOperationException("blanks add <count>: the count must be a positive number.");
        long cap = Util.ParseSize(opt.GetValueOrDefault("size", "96882130944"));
        long res = Util.ParseSize(opt.GetValueOrDefault("reserve", "0"));
        if (cap <= 0 || res < 0 || res >= cap) throw new InvalidOperationException("The reserve must be smaller than the disc size.");
        for (int n = 0; n < count; n++)
            cat.Blanks.Add(new BlankDisc { Id = NextBlankId(), Capacity = cap, Reserve = res, AddedUtc = DateTime.UtcNow });
        cat.Save();
        Console.WriteLine($"Added {count} blank disc(s) of {cap:N0} bytes, reserve {res:N0}.\n");
        return BlanksList();
    }

    if (sub == "reserve" && a.Count == 2)
    {
        long res = Util.ParseSize(a[1]);
        var targets = cat.Blanks.Where(b => sizeFilter == null || b.Capacity == sizeFilter).ToList();
        if (targets.Count == 0) { Console.WriteLine("No matching blank discs."); return 1; }
        var tooSmall = targets.FirstOrDefault(b => res >= b.Capacity);
        if (tooSmall != null)
            throw new InvalidOperationException($"A reserve of {res:N0} bytes is not smaller than {tooSmall.Id} ({tooSmall.Capacity:N0} bytes).");
        foreach (var blank in targets) blank.Reserve = res;
        cat.Save();
        Console.WriteLine($"Reserve set to {res:N0} bytes on {targets.Count} blank disc(s). (Discs that are already staged keep theirs.)\n");
        return BlanksList();
    }

    if (sub == "remove" && a.Count == 2)
    {
        int removed = a[1].Equals("all", StringComparison.OrdinalIgnoreCase)
            ? cat.Blanks.RemoveAll(b => sizeFilter == null || b.Capacity == sizeFilter)
            : cat.Blanks.RemoveAll(b => b.Id.Equals(a[1], StringComparison.OrdinalIgnoreCase));
        if (removed == 0) { Console.WriteLine("Nothing removed."); return 1; }
        cat.Save();
        Console.WriteLine($"Removed {removed} blank disc(s).");
        return 0;
    }

    throw new InvalidOperationException("Usage: blanks [list] | blanks add <count> [--size S] [--reserve R] | blanks reserve <R> [--size S] | blanks remove <id|all> [--size S]");
}

int BlanksList()
{
    if (cat.Blanks.Count == 0)
    {
        Console.WriteLine("No blank discs registered. Add some:  blanks add <count> --size <bytes|90GiB|100GB> [--reserve <size>]");
        return 0;
    }
    foreach (var g in cat.Blanks.GroupBy(b => (b.Capacity, b.Reserve)).OrderByDescending(g => g.Key.Capacity).ThenBy(g => g.Key.Reserve))
    {
        var ids = g.Select(b => b.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        string idText = ids.Count <= 8 ? string.Join(", ", ids) : $"{ids[0]} .. {ids[^1]}";
        Console.WriteLine($"{g.Count(),4} x {g.Key.Capacity,18:N0} bytes, reserve {g.Key.Reserve:N0} (usable {g.Key.Capacity - g.Key.Reserve:N0})   {idText}");
    }
    Console.WriteLine($"Total: {cat.Blanks.Count} disc(s), {cat.Blanks.Sum(b => b.Usable):N0} usable bytes.");
    return 0;
}

// ---- scan / import an already burned disc --------------------------------------------------

DriveInfo? FindDrive(string path)
{
    try
    {
        return DriveInfo.GetDrives()
            .Where(d => path.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
}

bool IsDriveRoot(string path, DriveInfo d) =>
    string.Equals(path.TrimEnd('\\', '/'), d.RootDirectory.FullName.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

static bool IsDiscMeta(string rel) =>
    rel.Equals("disc.json", StringComparison.OrdinalIgnoreCase) || rel.Equals("manifest.jsonl", StringComparison.OrdinalIgnoreCase) ||
    rel.StartsWith("_catalog/", StringComparison.OrdinalIgnoreCase) ||
    rel.StartsWith("System Volume Information/", StringComparison.OrdinalIgnoreCase) ||
    rel.StartsWith("$RECYCLE.BIN/", StringComparison.OrdinalIgnoreCase);

// Reads a mounted disc (or any folder laid out like one) and records it in the catalog.
//   --method auto|manifest|hash|quick
//       auto      manifest.jsonl if the disc has one, otherwise hash
//       manifest  trust the disc's manifest.jsonl (fast; entries are flagged "unverified" until 'verify')
//       hash      read and SHA-256 every file on the disc (slow, authoritative; also catches files nobody listed)
//       quick     no hashing: match path + size + modified time against the catalog (flagged "unverified")
//   --label X          label to use (otherwise disc.json, otherwise the drive's volume label)
//   --date yyyy-MM-dd  when it was burned
//   --capacity 100GB   size of the disc (optional)
//   --update           the label is already in the catalog: merge what is on the disc
//   --dry-run          show what would happen, change nothing
//   --write-disc-json  if the disc can still be written to, put disc.json (+ manifest.jsonl) on it
int ScanDisc(string mountArg)
{
    string mount = Path.GetFullPath(mountArg);
    if (!Directory.Exists(mount)) throw new InvalidOperationException($"Not a directory: {mount}");
    bool dry = opt.ContainsKey("dry-run");
    string method = opt.GetValueOrDefault("method", "auto").ToLowerInvariant();
    if (method is not ("auto" or "manifest" or "hash" or "quick"))
        throw new InvalidOperationException("--method must be auto, manifest, hash or quick.");
    var drive = FindDrive(mount);

    // 1. which disc is this?
    DiscInfo? info = null;
    string infoPath = Path.Combine(mount, "disc.json");
    if (File.Exists(infoPath))
    {
        try { info = Util.ReadJson<DiscInfo>(infoPath); }
        catch (JsonException) { Console.WriteLine("disc.json cannot be parsed - ignoring it."); }
    }
    string? label = opt.GetValueOrDefault("label");
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
        throw new InvalidOperationException("Cannot tell which disc this is (no disc.json, no volume label). Pass --label <label>.");
    if (label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        throw new InvalidOperationException($"'{label}' is not usable as a label.");
    if (info != null && !info.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"Warning: disc.json says {info.Label}, but the label {label} was requested.");

    var existing = cat.Discs.FirstOrDefault(d => d.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
    if (existing != null && !opt.ContainsKey("update"))
        throw new InvalidOperationException($"{existing.Label} is already in the catalog. Use --update to merge what is on this disc, or 'verify' to check it.");
    label = existing?.Label ?? label;
    Console.WriteLine($"Disc {label} (label from {how}){(existing != null ? ", updating its existing record" : "")}");

    DateTime burnedAt = opt.TryGetValue("date", out var dateText)
        ? Util.ParseDate(dateText)
        : existing?.BurnedUtc ?? info?.CreatedUtc ?? DateTime.UtcNow;

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
        throw new InvalidOperationException("There is no manifest.jsonl on this disc; use --method hash or quick.");
    Console.WriteLine($"Method: {method}; {onDisc.Count:N0} file(s), {onDisc.Sum(x => x.Size):N0} bytes on the disc.");

    var found = new List<DiscFile>();
    var problems = new List<string>();      // listed in the manifest but missing / different size, or unreadable
    var notListed = new List<string>();     // on the disc but not in the manifest

    if (method == "hash")
    {
        foreach (var f in onDisc)
        {
            Console.WriteLine($"hashing {f.Path} ({f.Size:N0} bytes)");
            try { found.Add(new DiscFile(f.Path, f.Size, f.Mtime, Util.HashFile(Path.Combine(mount, f.Path)), false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{f.Path}: {ex.Message}"); }
        }
    }
    else if (method == "manifest")
    {
        var diskByPath = onDisc.ToDictionary(x => x.Path, x => x, StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Util.ReadJsonl<ManifestLine>(manifestPath))
        {
            listed.Add(line.Path);
            if (!diskByPath.TryGetValue(line.Path, out var onThisDisc)) { problems.Add($"{line.Path}: listed in the manifest but not on the disc"); continue; }
            if (onThisDisc.Size != line.Size) { problems.Add($"{line.Path}: size differs from the manifest"); continue; }
            found.Add(new DiscFile(line.Path, onThisDisc.Size, onThisDisc.Mtime, line.Sha256.ToUpperInvariant(), true));
        }
        notListed = onDisc.Where(x => !listed.Contains(x.Path)).Select(x => x.Path).ToList();
    }
    else   // quick
    {
        foreach (var f in onDisc) found.Add(new DiscFile(f.Path, f.Size, f.Mtime, null, true));
    }

    // 3. connect what was found with what the catalog already knows
    var byPathIdx = cat.Files.ToLookup(f => f.RootName + "/" + f.RelPath, StringComparer.Ordinal);
    var byHashIdx = cat.Files.ToLookup(f => f.Sha256, StringComparer.OrdinalIgnoreCase);
    var disc = existing ?? new DiscRecord
    {
        Label = label,
        State = "burned",
        CreatedUtc = burnedAt,
        BurnedUtc = burnedAt,
        Note = $"imported from disc ({method})"
    };
    var manifest = new List<ManifestLine>();
    var unmatched = new List<string>();
    int sameVersion = 0, olderVersion = 0, otherPath = 0, created = 0, already = 0;

    foreach (var df in found)
    {
        FileRecord? rec;
        string sha;
        var candidates = byPathIdx[df.Path].ToList();

        if (df.Sha256 == null)   // quick: nothing was hashed, so the catalog entry has to match on path, size and time
        {
            rec = candidates.FirstOrDefault(c => c.Size == df.Size && Math.Abs((c.ModifiedUtc - df.Mtime).TotalSeconds) <= 2);
            if (rec == null) { unmatched.Add(df.Path); continue; }
            sha = rec.Sha256;
            sameVersion++;
        }
        else
        {
            sha = df.Sha256;
            rec = candidates.FirstOrDefault(c => c.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase));
            if (rec != null) sameVersion++;
            else if (candidates.Count > 0) { rec = candidates[0]; olderVersion++; }   // same path, other content: an older/newer version
            else
            {
                rec = byHashIdx[sha].FirstOrDefault();                                   // same content under another path (moved/renamed)
                if (rec != null) otherPath++;
            }

            if (rec == null)   // not known at all: create an entry (the source file is not available)
            {
                int slash = df.Path.IndexOf('/');
                string rootName = slash > 0 ? df.Path[..slash] : "(imported)";
                string relPath = slash > 0 ? df.Path[(slash + 1)..] : df.Path;
                string root = cat.Files.FirstOrDefault(f => f.RootName == rootName && !f.Root.StartsWith("imported:"))?.Root ?? "imported:" + label;
                rec = new FileRecord
                {
                    Root = root,
                    RootName = rootName,
                    RelPath = relPath,
                    Size = df.Size,
                    ModifiedUtc = df.Mtime,
                    Sha256 = sha,
                    AddedUtc = DateTime.UtcNow,
                    MissingSinceUtc = DateTime.UtcNow
                };
                cat.Files.Add(rec);
                created++;
            }
        }

        manifest.Add(new ManifestLine(df.Path, df.Size, sha));
        if (rec.Backups.Any(b => b.Disc == label && b.DiscPath == df.Path)) { already++; continue; }
        rec.Backups.Add(new BackupRef { Disc = label, DiscPath = df.Path, Sha256 = sha, BackedUpUtc = burnedAt, Unverified = df.Unverified });
    }
    int linked = LinkDuplicates();   // other catalog files with identical content are now covered by this disc too

    disc.FileCount = onDisc.Count;
    disc.UsedBytes = onDisc.Sum(x => x.Size);
    if (opt.TryGetValue("capacity", out var capText)) disc.Capacity = Util.ParseSize(capText);

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
    if (found.Any(x => x.Unverified)) Console.WriteLine("  Entries taken without hashing are marked unverified; 'verify " + label + " <mount>' will check them.");

    long? free = null;
    try { free = drive?.AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    bool appendable = free is { } fr && fr > (64L << 20);
    if (free is { } freeBytes)
        Console.WriteLine($"  free space reported for this drive: {freeBytes:N0} bytes" + (appendable ? " - the disc may still accept more data" : " - looks full or finalized"));

    if (dry) { Console.WriteLine("\nDry run - the catalog was not changed."); return 0; }

    if (existing == null) cat.Discs.Add(disc);
    if (opt.ContainsKey("write-disc-json"))
    {
        if (TryWriteDiscJson(mount, disc, manifest, opt.ContainsKey("overwrite"), out var why))
            Console.WriteLine("\ndisc.json (and manifest.jsonl if it was missing) written onto the disc. If your burning software needs it, close the session/disc now.");
        else
            Console.WriteLine($"\nCould not write disc.json: {why}. The label is recorded in the catalog only.");
    }
    cat.Save();
    return problems.Count > 0 ? 3 : 0;
}

bool TryWriteDiscJson(string mount, DiscRecord disc, List<ManifestLine> manifest, bool overwrite, out string why)
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
        Util.WriteJson(infoPath, new DiscInfo(disc.Label, disc.CreatedUtc, disc.FileCount, disc.UsedBytes));
        string manifestPath = Path.Combine(mount, "manifest.jsonl");
        if (!File.Exists(manifestPath) && manifest.Count > 0) Util.WriteJsonl(manifestPath, manifest);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        why = "the disc cannot be written to (" + ex.Message + ")";
        return false;
    }
    why = "";
    return true;
}

// Writes disc.json (and manifest.jsonl if missing) onto a mounted disc that can still be written to.
int DiscJson(string label, string mountArg)
{
    var disc = FindDisc(label);
    string mount = Path.GetFullPath(mountArg);
    if (!Directory.Exists(mount)) throw new InvalidOperationException($"Not a directory: {mount}");

    var manifest = cat.Files.SelectMany(f => f.Backups.Where(b => b.Disc == disc.Label))
        .GroupBy(b => b.DiscPath, StringComparer.Ordinal).Select(g => g.First())
        .Select(b => (Copy: b, FullPath: Path.Combine(mount, b.DiscPath)))
        .Where(x => File.Exists(x.FullPath))
        .Select(x => new ManifestLine(x.Copy.DiscPath, new FileInfo(x.FullPath).Length, x.Copy.Sha256))
        .ToList();

    if (TryWriteDiscJson(mount, disc, manifest, opt.ContainsKey("overwrite"), out var why))
    {
        Console.WriteLine($"disc.json written for {disc.Label} ({manifest.Count:N0} file(s) found on the disc).");
        return 0;
    }
    Console.WriteLine($"Could not write disc.json: {why}.");
    return 3;
}

// A file needs backing up if it is still on the source and no live disc (staged or burned) holds its CURRENT hash.
PlanResult MakePlan()
{
    // Hash-based dedup: content that is already on any disc (under any path, even from a file that has since
    // moved or been deleted) is not pending, and among pending files with identical content only one is stored.
    var dead = DeadDiscs();   // copies on lost/destroyed discs do not count
    var index = BackupIndex();
    var pending = cat.Files
        .Where(f => f.MissingSinceUtc == null && !f.Backups.Any(b => !dead.Contains(b.Disc) && b.Sha256 == f.Sha256) && !index.ContainsKey(f.Sha256))
        .GroupBy(f => f.Sha256)
        .Select(g => g.OrderBy(f => f.RootName + "/" + f.RelPath, StringComparer.Ordinal).First())
        .ToList();

    // Allocate to the registered blank discs; whatever does not fit is reported, never silently dropped.
    long largest = cat.Blanks.Select(b => b.Usable).DefaultIfEmpty(0).Max();
    var tooBig = cat.Blanks.Count == 0 ? new List<FileRecord>() : pending.Where(f => f.Size > largest).ToList();
    var tooBigSet = tooBig.ToHashSet();
    var (bins, leftover) = Packer.Allocate(pending.Where(f => !tooBigSet.Contains(f)), cat.Blanks);
    var unplaced = cat.Blanks.Count == 0 ? pending.ToList() : leftover.Concat(tooBig).ToList();
    return new PlanResult(bins, unplaced, tooBig, pending);
}

// hash -> one existing copy on a disc (prefers burned over staged, real copies over aliases)
Dictionary<string, BackupRef> BackupIndex()
{
    var idx = new Dictionary<string, BackupRef>(StringComparer.OrdinalIgnoreCase);
    var dead = DeadDiscs();
    foreach (var b in cat.Files.SelectMany(f => f.Backups).Where(b => !dead.Contains(b.Disc))
                               .OrderByDescending(b => b.BackedUpUtc != null).ThenBy(b => b.Alias))
        idx.TryAdd(b.Sha256, b);
    return idx;
}

// Gives every file whose content is already on a disc a backup reference (Alias = true) to that copy.
int LinkDuplicates()
{
    var dead = DeadDiscs();
    var idx = BackupIndex();
    int n = 0;
    foreach (var f in cat.Files.Where(f => f.MissingSinceUtc == null && !f.Backups.Any(b => !dead.Contains(b.Disc) && b.Sha256 == f.Sha256)))
        if (idx.TryGetValue(f.Sha256, out var r))
        {
            f.Backups.Add(new BackupRef { Disc = r.Disc, DiscPath = r.DiscPath, Sha256 = r.Sha256, BackedUpUtc = r.BackedUpUtc, Alias = true });
            n++;
        }
    return n;
}

void PrintTooBig(List<FileRecord> tooBig)
{
    if (tooBig.Count == 0) return;
    Console.WriteLine("These files are larger than the largest registered blank disc and cannot be placed without splitting:");
    foreach (var f in tooBig) Console.WriteLine($"  {f.RootName}/{f.RelPath}  ({f.Size:N0} bytes)");
    Console.WriteLine();
}

string NextLabel()
{
    int n = cat.Discs.Select(d => d.Label.StartsWith("BDXL-", StringComparison.OrdinalIgnoreCase) && int.TryParse(d.Label.AsSpan(5), out var v) ? v : 0)
        .DefaultIfEmpty(0).Max() + 1;
    return $"BDXL-{n:D4}";
}

string NextBlankId()
{
    int n = cat.Blanks.Select(b => b.Id.StartsWith('B') && int.TryParse(b.Id.AsSpan(1), out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1;
    return $"B{n:D4}";
}

DiscRecord FindDisc(string label) =>
    cat.Discs.FirstOrDefault(d => d.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException($"Unknown disc '{label}'.");

int Usage()
{
    Console.WriteLine("""
        bdxl <command> [args] [--catalog dir] [--reserve bytes]

          scan   <sourceDir> [--name alias]   index files and hash new/changed ones
          plan                                show which pending files would go on which discs
          stage  <destDir> [--max-discs N] [--min-fill 0.9] [--with-catalog]
                                              copy the next N discs into <destDir>/BDXL-xxxx
          burned <label> [--date yyyy-MM-dd]  mark a staged disc as burned
          verify <label> <mountedDiscPath>    re-hash a disc against the catalog
          discard <label>                     forget a staged (unburned) disc
          find   <text|hashPrefix>            where is this file backed up?
          status                              summary
          discs                               list all discs and their state

          lost <label> [--reason text]        disc is lost: its copies stop counting, files become pending again
          destroyed <label> [--reason text]   same, but final (cannot be un-marked)
          found <label>                       a 'lost' disc turned up again
          restage <label> <destDir> [--from dirA;dirB] [--allow-partial]
                                              rebuild the same disc (same label, same files) for burning again
          restore <destDir> <text|hashPrefix|*> --mount F:\;G:\ [--disc label] [--source name] [--flat] [--overwrite]
                                              copy files back from mounted discs into <destDir>

          catbackup <destDir> [--parity-percent 25]
                                              snapshot of the catalog + Reed-Solomon repair data
          catverify <backupDir> [--repair]    check (and fix) a catalog backup
          catrestore <backupDir> [--force]    put a catalog backup back into --catalog dir

          stage ... --with-catalog            also write a catalog backup into <disc>/_catalog
        """);
    return 1;
}

// ---------------------------------------------------------------------------------------------
// Types (must stay below the top-level statements)
// ---------------------------------------------------------------------------------------------

class BackupRef
{
    public string Disc { get; set; } = "";
    public string DiscPath { get; set; } = "";     // path of the file on the disc
    public string Sha256 { get; set; } = "";       // hash of the version stored on that disc
    public DateTime? BackedUpUtc { get; set; }     // null until the disc is marked as burned
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unverified { get; set; }           // true = recorded from a manifest / catalog match without re-hashing the disc ('verify' clears it)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Alias { get; set; }               // true = identical content already stored on that disc under another path (dedup)
}

class FileRecord
{
    public string Root { get; set; } = "";         // full source folder that was scanned
    public string RootName { get; set; } = "";     // top-level folder name used on discs
    public string RelPath { get; set; } = "";      // path relative to Root, '/' separators
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string Sha256 { get; set; } = "";       // hash of the current version
    public DateTime AddedUtc { get; set; }
    public DateTime? MissingSinceUtc { get; set; } // set when the file disappeared from the source
    public List<BackupRef> Backups { get; set; } = new();   // history of every copy ever staged/burned
}

class DiscRecord
{
    public string Label { get; set; } = "";        // e.g. BDXL-0001 (write it on the disc!)
    public string State { get; set; } = "staged";  // staged | burned | lost | destroyed
    public DateTime CreatedUtc { get; set; }
    public DateTime? BurnedUtc { get; set; }
    public DateTime? LostUtc { get; set; }         // when it was marked lost / destroyed
    public DateTime? RestagedUtc { get; set; }     // last time the disc folder was rebuilt for re-burning
    public string? Note { get; set; }
    public long? Capacity { get; set; }            // size of the blank disc it was written to (if known)
    public long? Reserve { get; set; }             // part of that capacity that was deliberately left free
    public int FileCount { get; set; }
    public long UsedBytes { get; set; }
}

record ManifestLine(string Path, long Size, string Sha256);
record DiscInfo(string Label, DateTime CreatedUtc, int FileCount, long UsedBytes);
record RepairMeta(string File, long Length, string FileSha256, int BlockSize, int DataBlocks, int ParityBlocks, string[] BlockSha256);

enum RsStatus { Ok, Repaired, Unrecoverable }

// GF(256) arithmetic (polynomial 0x11D) with cached multiplication tables.
static class Gf
{
    static readonly byte[] Exp = new byte[512];
    static readonly byte[] Log = new byte[256];
    static readonly byte[]?[] Tables = new byte[256][];

    static Gf()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    public static byte Inv(byte a) => Exp[255 - Log[a]];   // a must be non-zero

    public static byte[] Table(byte c)
    {
        var t = Tables[c];
        if (t != null) return t;
        t = new byte[256];
        for (int v = 0; v < 256; v++) t[v] = Mul(c, (byte)v);
        Tables[c] = t;
        return t;
    }
}

// Reed-Solomon erasure code (Cauchy matrix over GF(256)), PAR2-style:
// a file is cut into k <= 200 equal blocks, m parity blocks are added, and a SHA-256 per block tells which
// blocks are damaged. Any combination of up to m damaged blocks (data or parity) can be rebuilt.
static class Rs
{
    const int MaxData = 200;

    static string Hex(ReadOnlySpan<byte> d) => Convert.ToHexString(SHA256.HashData(d));
    static byte Coef(int row, int col, int m) => Gf.Inv((byte)(row ^ (m + col)));   // row < m <= m + col, so never 0

    static byte[][] Split(byte[] data, int k, int blockSize)
    {
        var blocks = new byte[k][];
        for (int i = 0; i < k; i++)
        {
            var blk = new byte[blockSize];
            long off = (long)i * blockSize;
            int n = (int)Math.Max(0, Math.Min(blockSize, data.Length - off));
            if (n > 0) Buffer.BlockCopy(data, (int)off, blk, 0, n);
            blocks[i] = blk;
        }
        return blocks;
    }

    static byte[] Encode(byte[][] blocks, int m, int blockSize)
    {
        var parity = new byte[m * blockSize];
        for (int j = 0; j < m; j++)
        {
            int off = j * blockSize;
            for (int i = 0; i < blocks.Length; i++)
            {
                var t = Gf.Table(Coef(j, i, m));
                var src = blocks[i];
                for (int x = 0; x < blockSize; x++) parity[off + x] ^= t[src[x]];
            }
        }
        return parity;
    }

    public static RepairMeta Protect(string name, byte[] data, int pct, out byte[] parity)
    {
        string fileHash = Hex(data);
        if (data.Length == 0)
        {
            parity = Array.Empty<byte>();
            return new RepairMeta(name, 0, fileHash, 0, 0, 0, Array.Empty<string>());
        }

        long raw = Math.Max(4096, (data.Length + MaxData - 1L) / MaxData);
        int blockSize = (int)((raw + 4095) / 4096 * 4096);
        int k = (int)((data.Length + (long)blockSize - 1) / blockSize);          // <= 200
        int m = Math.Clamp((int)Math.Ceiling(k * pct / 100.0), 2, 255 - k);       // k + m <= 255

        var blocks = Split(data, k, blockSize);
        parity = Encode(blocks, m, blockSize);

        var hashes = new string[k + m];
        for (int i = 0; i < k; i++) hashes[i] = Hex(blocks[i]);
        for (int j = 0; j < m; j++) hashes[k + j] = Hex(parity.AsSpan(j * blockSize, blockSize));
        return new RepairMeta(name, data.Length, fileHash, blockSize, k, m, hashes);
    }

    // Parity is non-null only when the repair data itself had damage and has been regenerated.
    public static (RsStatus Status, byte[] Data, byte[]? Parity, int BadData, int BadParity) Check(RepairMeta meta, byte[]? data, byte[]? par)
    {
        int k = meta.DataBlocks, m = meta.ParityBlocks, B = meta.BlockSize;

        if (k == 0)   // empty file
        {
            if (data != null && data.Length == 0) return (RsStatus.Ok, data, null, 0, 0);
            return (RsStatus.Repaired, Array.Empty<byte>(), null, 1, 0);
        }

        var blocks = Split(data ?? Array.Empty<byte>(), k, B);
        var badD = new List<int>();
        for (int i = 0; i < k; i++)
            if (Hex(blocks[i]) != meta.BlockSha256[i]) badD.Add(i);

        var pblk = new byte[m][];
        var badP = new HashSet<int>();
        for (int j = 0; j < m; j++)
        {
            var blk = new byte[B];
            long off = (long)j * B;
            if (par != null && par.Length >= off + B) Buffer.BlockCopy(par, (int)off, blk, 0, B);
            pblk[j] = blk;
            if (Hex(blk) != meta.BlockSha256[k + j]) badP.Add(j);
        }

        if (data != null && data.Length == meta.Length && badD.Count == 0 && badP.Count == 0 && Hex(data) == meta.FileSha256)
            return (RsStatus.Ok, data, null, 0, 0);

        var unrecoverable = (RsStatus.Unrecoverable, data ?? Array.Empty<byte>(), (byte[]?)null, badD.Count, badP.Count);
        var goodP = Enumerable.Range(0, m).Where(j => !badP.Contains(j)).ToList();
        if (badD.Count > goodP.Count) return unrecoverable;

        if (badD.Count > 0)
        {
            int d = badD.Count;
            var damagedSet = badD.ToHashSet();
            var rows = goodP.Take(d).ToArray();

            var a = new byte[d][];
            for (int r = 0; r < d; r++)
            {
                a[r] = new byte[d];
                for (int c = 0; c < d; c++) a[r][c] = Coef(rows[r], badD[c], m);
            }
            var inv = Invert(a, d);
            if (inv == null) return unrecoverable;

            // rhs[r] = parity_row - contribution of all intact data blocks = sum over damaged blocks
            var rhs = new byte[d][];
            for (int r = 0; r < d; r++)
            {
                var acc = (byte[])pblk[rows[r]].Clone();
                for (int i = 0; i < k; i++)
                {
                    if (damagedSet.Contains(i)) continue;
                    var t = Gf.Table(Coef(rows[r], i, m));
                    var src = blocks[i];
                    for (int x = 0; x < B; x++) acc[x] ^= t[src[x]];
                }
                rhs[r] = acc;
            }

            for (int c = 0; c < d; c++)
            {
                var rebuilt = new byte[B];
                for (int r = 0; r < d; r++)
                {
                    byte coef = inv[c][r];
                    if (coef == 0) continue;
                    var t = Gf.Table(coef);
                    var src = rhs[r];
                    for (int x = 0; x < B; x++) rebuilt[x] ^= t[src[x]];
                }
                blocks[badD[c]] = rebuilt;
            }
        }

        for (int i = 0; i < k; i++)
            if (Hex(blocks[i]) != meta.BlockSha256[i]) return unrecoverable;

        var fixedData = new byte[meta.Length];
        for (int i = 0; i < k; i++)
        {
            long off = (long)i * B;
            Buffer.BlockCopy(blocks[i], 0, fixedData, (int)off, (int)Math.Min(B, meta.Length - off));
        }
        if (Hex(fixedData) != meta.FileSha256) return unrecoverable;

        byte[]? newParity = badP.Count > 0 ? Encode(blocks, m, B) : null;
        return (RsStatus.Repaired, fixedData, newParity, badD.Count, badP.Count);
    }

    // Gauss-Jordan inverse over GF(256); null if singular.
    static byte[][]? Invert(byte[][] a, int n)
    {
        var w = new byte[n][];
        for (int r = 0; r < n; r++)
        {
            w[r] = new byte[2 * n];
            Array.Copy(a[r], w[r], n);
            w[r][n + r] = 1;
        }
        for (int col = 0; col < n; col++)
        {
            int piv = -1;
            for (int r = col; r < n; r++)
                if (w[r][col] != 0) { piv = r; break; }
            if (piv < 0) return null;
            (w[col], w[piv]) = (w[piv], w[col]);

            byte iv = Gf.Inv(w[col][col]);
            for (int x = 0; x < 2 * n; x++) w[col][x] = Gf.Mul(w[col][x], iv);
            for (int r = 0; r < n; r++)
            {
                if (r == col || w[r][col] == 0) continue;
                byte f = w[r][col];
                for (int x = 0; x < 2 * n; x++) w[r][x] ^= Gf.Mul(f, w[col][x]);
            }
        }
        var inv = new byte[n][];
        for (int r = 0; r < n; r++)
        {
            inv[r] = new byte[n];
            Array.Copy(w[r], n, inv[r], 0, n);
        }
        return inv;
    }
}

// A blank disc the user owns and has registered with 'blanks add'.
class BlankDisc
{
    public string Id { get; set; } = "";
    public long Capacity { get; set; }             // bytes
    public long Reserve { get; set; }              // bytes deliberately kept free (filesystem overhead, catalog backup, ...)
    public DateTime AddedUtc { get; set; }
    [JsonIgnore] public long Usable => Capacity - Reserve;
}

// What one blank disc would receive.
class Bin
{
    public Bin(BlankDisc blank) { Blank = blank; }
    public BlankDisc Blank { get; }
    public long Usable => Blank.Usable;
    public List<FileRecord> Files { get; } = new();
    public long Used { get; private set; }
    public long Free => Usable - Used;
    public void Add(FileRecord f) { Files.Add(f); Used += f.Size; }
}

record PlanResult(List<Bin> Bins, List<FileRecord> Unplaced, List<FileRecord> TooBig, List<FileRecord> Pending);

// A file found on an already burned disc by 'scandisc'.
record DiscFile(string Path, long Size, DateTime Mtime, string? Sha256, bool Unverified);

static class Packer
{
    // Best Fit Decreasing onto a FIXED set of blank discs of possibly different sizes: largest file first,
    // each into the disc that is left with the least free space. Files that fit nowhere are returned, not dropped.
    public static (List<Bin> Bins, List<FileRecord> Unplaced) Allocate(IEnumerable<FileRecord> files, IReadOnlyList<BlankDisc> blanks)
    {
        var bins = blanks.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => new Bin(b)).ToList();
        var byFree = new SortedSet<(long Free, int Index)>();
        for (int i = 0; i < bins.Count; i++) byFree.Add((bins[i].Free, i));
        var unplaced = new List<FileRecord>();

        foreach (var f in files.OrderByDescending(f => f.Size)
                               .ThenBy(f => f.RootName + "/" + f.RelPath, StringComparer.Ordinal))
        {
            (long Free, int Index)? best = null;
            foreach (var candidate in byFree.GetViewBetween((f.Size, int.MinValue), (long.MaxValue, int.MaxValue)))
            {
                best = candidate;   // first element of the view = smallest free space that still fits
                break;
            }

            if (best is not { } chosen) { unplaced.Add(f); continue; }
            byFree.Remove(chosen);
            bins[chosen.Index].Add(f);
            byFree.Add((bins[chosen.Index].Free, chosen.Index));
        }
        return (bins, unplaced);
    }
}

class Catalog
{
    public string Dir { get; private init; } = "";
    public List<FileRecord> Files { get; private init; } = new();
    public List<DiscRecord> Discs { get; private init; } = new();
    public List<BlankDisc> Blanks { get; private init; } = new();

    public static Catalog Load(string dir) => new()
    {
        Dir = dir,
        Files = Util.ReadJsonl<FileRecord>(Path.Combine(dir, "files.jsonl")),
        Discs = Util.ReadJsonl<DiscRecord>(Path.Combine(dir, "discs.jsonl")),
        Blanks = Util.ReadJsonl<BlankDisc>(Path.Combine(dir, "blanks.jsonl"))
    };

    // Written atomically (temp file + rename) and sorted, so the files stay stable and diff-friendly.
    public void Save()
    {
        Util.WriteJsonl(Path.Combine(Dir, "files.jsonl"),
            Files.OrderBy(f => f.Root, StringComparer.Ordinal).ThenBy(f => f.RelPath, StringComparer.Ordinal));
        Util.WriteJsonl(Path.Combine(Dir, "discs.jsonl"),
            Discs.OrderBy(d => d.Label, StringComparer.Ordinal));
        Util.WriteJsonl(Path.Combine(Dir, "blanks.jsonl"),
            Blanks.OrderBy(b => b.Id, StringComparer.Ordinal));
    }
}

static class Util
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,          // keep ą, ł, ż... readable
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly string[] CatalogFiles = { "files.jsonl", "discs.jsonl", "blanks.jsonl" };

    // "96882130944", "25GB" (decimal), "90GiB" (binary), "500MB", "1.5TB" ...
    public static long ParseSize(string text)
    {
        string s = text.Trim().Replace("_", "").Replace(",", "");
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0 || !double.TryParse(s[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            throw new InvalidOperationException($"Cannot parse size '{text}'.");
        double mult = s[i..].Trim().ToUpperInvariant() switch
        {
            "" or "B" => 1,
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            "K" or "KIB" => 1024,
            "M" or "MIB" => 1048576,
            "G" or "GIB" => 1073741824,
            "T" or "TIB" => 1099511627776,
            _ => throw new InvalidOperationException($"Unknown size unit in '{text}'.")
        };
        return (long)Math.Round(n * mult);
    }

    static readonly JsonSerializerOptions JsonPretty = new(Json) { WriteIndented = true };

    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonPretty));
    }

    public static T? ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);

    public static void WriteJsonl<T>(string path, IEnumerable<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllLines(tmp, items.Select(i => JsonSerializer.Serialize(i, Json)));
        File.Move(tmp, path, overwrite: true);
    }

    public static List<T> ReadJsonl<T>(string path) =>
        !File.Exists(path)
            ? new List<T>()
            : File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l))
                  .Select(l => JsonSerializer.Deserialize<T>(l, Json)!).ToList();

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    // Copies a file and hashes the bytes that were actually written, in a single pass.
    public static string CopyAndHash(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            hash.AppendData(buf, 0, n);
            output.Write(buf, 0, n);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static DateTime ParseDate(string s)
    {
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var d))
            throw new InvalidOperationException($"Cannot parse date '{s}' (use yyyy-MM-dd).");
        return d;
    }
}