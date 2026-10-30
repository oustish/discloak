# bdxl – handoff notes (state as of 2026-10-29)

## Status
- C# / .NET 10 console app (`net10.0`), 57 source files under `src/`, single namespace `Bdxl`, classic `Main` (no top-level statements).
- Builds cleanly (0 warnings). Smoke-tested by hand on 2026-10-29 in a temp folder: every command was run, including
  encryption, tamper detection, lost/restage, catalog rollback, backup repair and scandisc. Automated tests: `tests/Bdxl.Tests` (xUnit, see below).
- Fixed during that smoke test: UTF-8 console output, `--date` stored one day early (local midnight to UTC), false
  "DATA LOST" for moved files (lost + status), `verify` writing a history snapshot even when nothing changed,
  `find`/`restore` not matching `alias/path` or on-disc names, restore listing ignored files as lost, and `stage`
  now warns (exit 2) when the staged folder (with manifest/_catalog) is larger than the disc.
- Later the same day: `--split` added (see below) and the last small issues fixed (`plan` disc estimate ignores
  files larger than any disc, `ignore remove` of an unknown rule exits 1, `stage` exits 2 when nothing could be staged).
- Since then: one class per command (`ICommand`), one file per model type, xUnit tests, and encrypted discs hide
  everything but `disc.json`: file names, manifest and `_catalog` backup (see Key rules).
  Then `stage --append <label>`: a burned disc that is not full gets another session (see Key rules).
  `scan` prints every file as soon as it is hashed (progress by bytes, speed, time left), with a status line rewritten in place while large files are hashed.
- **Encrypted catalog (phase 1 of `ENCRYPTION.md`, the agreed design):** the catalog is encrypted with a random master
  key that `bdxl.key` holds wrapped by the password (Argon2id) and optionally a recovery key; `init`, migration of
  plaintext catalogs, `password change`, `password check` (one password against any bdxl.key, no agent), `recovery-key`, `catalog export/import`, password-free catalog backups.
  `unlock` hands the master key to a background agent (memory only, idle/max timeouts, Win+L/sleep), `lock` takes it back.
  Encrypted discs use the same master key and carry `bdxl.key` + `README.txt` (phase 2); `init --key-from <disc>`.
  Every staged disc is encrypted (phase 3); plain discs only come from `scandisc` (older discs) and are still
  verified, restored and restaged as they are. Compression per location (phase 4, `compression`) completes the design.
  Key rotation (`password change --rotate-key`, a `KeyRing` in `bdxl.key` format `bdxl-key-2`) came after it.

## Build / run
```
dotnet build -c Release
dotnet publish -p:PublishProfile=win-x64   # single-file ../publish/bdxl.exe (Properties/PublishProfiles/win-x64.pubxml)
dotnet test ../BdxlPlanner.slnx -c Release   # from bdxl/; 219 tests, a few seconds
dotnet run -- help                 # all commands
dotnet run -- <command> --help     # details
dotnet run -- workflow             # sample session
```
Every command takes `--catalog <dir>` (default `./catalog`). Start with `init`; the password comes from `BDXL_PASSWORD`,
`--password-env VAR` or is typed (every command that loads the catalog needs it). After `unlock` the agent provides it.

## Source layout
| Folder | Contents |
|---|---|
| `src/Program.cs` | `Main`: parse args → find command → load + integrity-check catalog → run. Exit codes: 0 ok, 1 error, 2 partial/not everything fits, 3 problems found, 4 catalog broken, 130 interrupted (Ctrl+C) |
| `src/Cli/Options.cs` | Arg parser. `Flags` set = options without a value (add new boolean options there!) |
| `src/Cli/ICommand.cs` | `ICommand`: `Name`, `Group` (a `CommandGroup` constant), `Summary`, `Usage`, `Details` (help text), `AllowBrokenCatalog` / `NeedsCatalog` (default false / true), `Run(Ctx)` |
| `src/Cli/CommandTable.cs` | The list of command instances; its order is the order of `bdxl help` |
| `src/Cli/Ui.cs` | `Confirm` (`--yes`), password prompt (`BDXL_PASSWORD` or `--password-env VAR`, else hidden typing); a new password: typed, then copied back from the password manager, then confirmed as saved |
| `src/Cli/Interrupt.cs` | Ctrl+C: `Interruptible` commands get `Interrupt.Requested` and stop cleanly; others (or a 2nd Ctrl+C) exit at once with a note. `Catalog.Save` runs inside `Interrupt.Critical()`, so a save is never cut off |
| `src/Model/` | One file per type: `FileRecord`, `BackupRef`, `DiscRecord`, `BlankDisc`, `ManifestLine`, `DiscInfo`, `AppendSession`, `RepairMeta`, `RsStatus`, `Piece`, `Bin`, `PlanResult`, `DiscFile`, `UserException` |
| `src/Model/Settings.cs` | `settings.json`: label prefix + never-reset counter, currency, history-keep, default-threads, ignore rules |
| `src/Storage/Catalog.cs` | Load/Save (encrypted), integrity (`catalog.hash.json` + HMAC), snapshots in `history/`, rollback |
| `src/Storage/JsonStore.cs`, `Hashing.cs` | JSONL/JSON read+write (atomic via .tmp), SHA-256 helpers |
| `src/Core/` | `Packer` (best-fit-decreasing onto fixed blank discs), `GitIgnore`, `FileCrypto` (`BDXLENC2`, +`PasswordPolicy`), `MasterKey` (+`KeyPurpose`, HKDF object keys), `KeyRing` (every master key so far, oldest first; current = last), `KeyFile` (`bdxl.key`, envelopes), `Kdf` (Argon2id via NSec/libsodium, calibration), `RecoveryKey` (text form with checksum), `Compression` (+`CompressionSetting`: per-alias mode/level, auto's extension list, signatures, planned sizes), `ReedSolomon` (`Gf`, `Rs`), `SizeText` |
| `src/Services/` | `Ctx` (what every command gets; `Arg(i, usage)`), `Archive` (dedup, planning, labels, ignore application), `DiscFiles` (manifest + disc.json [+ bdxl.key + README.txt], encrypted manifest, hidden names, writing onto a mounted disc), `AppendTarget` (free space of a disc for `--append`), `CatalogSetup` (unlock, `init`, crash-safe migration of plaintext catalogs) |
| `src/Agent/` | `AgentServer` (keys per catalog id, timeouts, Win+L/sleep via `SystemEvents`, sweep every 5 s: a late sweep = the computer slept), `AgentClient`, `AgentProtocol` (length-prefixed JSON over a `CurrentUserOnly` named pipe, protocol version, pipe name per user or `BDXL_AGENT_PIPE`), `AgentLauncher` (copies the program to `%LOCALAPPDATA%\bdxl\agent\<build>`, starts it detached and outside the terminal's job) |
| `src/Commands/` | One file per command: `<Name>Command.cs`, a sealed class implementing `ICommand` with its own help text |

## Adding a command
1. `src/Commands/FooCommand.cs`: `public sealed class FooCommand : ICommand` with Name/Group/Summary/Usage/Details and `Run`.
2. Add `new FooCommand()` to `CommandTable.All` where it should appear in `bdxl help`.
3. New boolean options go into `Options.Flags`.
4. A long-running command that can stop cleanly: `Interruptible => true`, check `Interrupt.Requested` at safe points,
   save, say what was left undone and return `Interrupt.ExitCode`. Long copies/hashes/decryptions take
   `Interrupt.Token` (checked every 1 MiB, throws OperationCanceledException). Interruptible now: scan, stage,
   restage, restore, verify, scandisc. On Ctrl+C: `stage` finishes the current disc/session with what was copied;
   `restage` keeps `<label>.partial` (reuse with --from), catalog unchanged; `restore` stops after the file;
   `verify` reports what was checked; `scandisc --method hash` records nothing.

Shared pieces: `lost` and `destroyed` both call `MarkDeadDisc.Run`; `stage --with-catalog` calls `CatalogCommand.WriteBackup`;
`stage`/`restage` write `DiscFiles.Write`, `scandisc`/`discjson` use `DiscFiles.TryWriteDiscJson`.
`Archive.CopiesOn(label)` = one (file, copy) per stored path of a disc, preferring the file it was stored for over aliases.

## Key rules / invariants
- **Catalog files:** `files.NNN.jsonl.enc` (the file list, `FilesPerShard` = 200k records per part, a part never
  spans two locations), `discs.jsonl.enc`, `blanks.jsonl.enc`, `settings.json.enc`, each `BDXLENC2` with purpose
  `catalog` and zstd level 3 (`Catalog.CompressionLevel`), plus `bdxl.key`. Always list them with
  `Catalog.StoredFilesIn(dir)` (integrity, snapshots, rollback, backups, migration), never a fixed list.
  `files.jsonl.enc` = the whole list in catalogs from before the split (read, split on the next save); since then it
  holds `TooOldMarker`, a non-JSON line, so an older build fails with exit 4 instead of loading an empty file list.
  `catalog.hash.json` = SHA-256 of each stored file + key id + save counter + HMAC (key from the master key).
  Every `Catalog.Save()` serializes each file in memory (no single buffer near 2 GiB any more) and encrypts straight
  into it - unless its plaintext hash equals the one loaded/last saved and it is under the current key (skipped).
  `Load` reads the file-list parts in parallel and parses lines while decrypting (`JsonLinesSink`: no part is ever
  whole in memory), and shares Root/RootName/Disc strings (and a current copy's Sha256) between records. `Save`
  first only hashes each file (`HashingSink`) and skips unchanged ones; changed ones are serialized again straight
  into the encryption through a `Pipe`, parts in parallel. `ICommand.NeedsFilesFor(opt) = false` loads without the file
  list (`Catalog.FilesLoaded`; Save keeps its files). Measured, 1M files: load ~2.7 s, live heap ~806 B/file,
  peak ~1.5 GB; light commands 0.2 s. Next step if needed: hashes as 32 bytes instead of 64-char strings (~-30%).
- **Mirror** (`Storage/Mirror.cs`): `mirror.json` (plain) in the catalog folder names it; `Program` runs `Mirror.Check` after
  loading: not in step -> commands are refused (exit 4) unless `ICommand.ReadOnlyFor(opt)` (they run, warned, with
  `Catalog.SaveBlockedBy` set as a backstop) or `FixesMirror(opt)` (`catalog mirror sync|restore`, `settings set
  mirror`). `Catalog.Save` ends with `Mirror.Sync` (trusts the mirror's hash list: it was checked at the start);
  explicit sync/restore hash the real files (`CopyCatalog(trustTarget: false)`). Commands that write `bdxl.key` or
  restore files without `Save` call `Mirror.Sync` themselves (password change after `RequireInSync`, recovery-key,
  rollback, backup restore). The mirror never gets history or `mirror.json`.
- **Keys:** `Program.OpenCatalog`: no catalog = "run init"; plaintext catalog = migrate after confirmation;
  otherwise `CatalogSetup.Unlock` (env/typed, 3 tries). `Ctx.Key` is the master key; `Catalog.Key` saves with it.
  `bdxl.key` is never in `history/`; a password change rewrites only `bdxl.key`. `ICommand.NeedsCatalogFor(opt)`
  lets `catalog backup verify/restore` run without unlocking (restore unlocks the right key file itself).
- **Key ring:** `Catalog.Keys` / `Ctx.Keys` is the `KeyRing`; `Ctx.Key` = its current key (everything new is encrypted
  with it). Decryption finds the key by id (`FileCrypto.Decrypt(..., KeyRing)`, `CheckIntegrity` via the key id in
  `catalog.hash.json`). Discs: `c.Keys.For(disc.KeyId)` for `--append`, `restage`, `discjson`; `Contains` for checks.
  `password change --rotate-key` adds a key, replaces the recovery key, writes `bdxl.key` first, then re-saves the
  catalog and locks the agent (protocol 2 carries the ring; a stale ring is ignored: its current key id differs).
- **Agent:** `CatalogSetup.Unlock` asks the agent first (`BDXL_AGENT=off` skips it; the tests set that). The
  agent (hidden command `agent`) refuses to start if its pipe exists, keeps one listening pipe instance at all times,
  exits when its last key is locked (or if no key arrives within a minute), logs to `%LOCALAPPDATA%\bdxl\agent\agent.log`
  without key material. `unlock` replaces an agent of another protocol version. `lock` needs no password.
- **Migration:** built in `.encrypting/`, plaintext moved to `.plaintext/`, encrypted files moved in, `bdxl.key` last.
  No `bdxl.key` = undo (`RecoverInterruptedMigration`); leftovers + plaintext history snapshots are finished by
  `FinishMigration` (snapshots that failed their old hash check get `unverified`).
- **Integrity states:** `Fresh` (empty), `Ok`, `Broken` (only commands with `allowBrokenCatalog` run; `Save()` throws).
  Damage shows without the key (`CheckIntegrity(dir, null)`); mixed/altered files fail the HMAC.
- **Versioning:** the first `Save()` in a process copies the on-disk catalog plus its hash file to
  `history/<yyyyMMdd-HHmmss>-<command>/`. That is one copy per command, pruned to `HistoryKeep`.
- **Dedup:** content identity is `FileRecord.Sha256`, the hash of the plaintext.
  - Pending = still on the source, not ignored, already hashed, and fewer live discs (staged or burned) hold a complete
    copy than `Settings.CopiesFor(alias)` (max over the locations holding that content; `Archive.WantedCopies`).
    `Archive.CompleteCopies` = whole copies on distinct discs + the best split layout's min discs per part. One piece
    per missing copy (`Piece` is a record: equal pieces are separate copies); `Packer.Allocate(groupOf, holds)` keeps
    pieces of one content on different bins (only when > 1 copy wanted, or for `--append`, where `holds` excludes the
    target disc if it has the content). Reporting (`status`, `copies list`, `find`, `lost`) counts burned copies only
    (`Archive.BurnedCopies`, `TooFewCopies`). `restore` takes each piece from a mounted disc if any copy is there.
  - Duplicates get a `BackupRef` with `Alias = true` that points at the stored copy (`Archive.LinkDuplicates`).
  - "Live" disc = state is not `lost` or `destroyed`.
- **Ignored files:** `Sha256 = ""` (never hashed), `Ignored = true`. Any code that uses the hash must skip empty hashes.
  `restage` ignores the `Ignored` flag on purpose.
- **Disc encryption (every staged disc; `--encrypt` is accepted and ignored):** everything is encrypted with the catalog's master key (`BDXLENC2`,
  purpose `file` for data, `manifest` for the manifest); readable without the password: `disc.json` (with
  `catalogId`/`keyId`), `bdxl.key` (copied from the catalog at staging: the password of that day) and `README.txt`
  (`DiscFiles.Readme`: enough to write a decryptor).
  - Stored path = hidden name `data/NNN/NNNNNN.enc` (`DiscFiles.HiddenName`, numbered in staging order, 1000 per
    folder; no source mtime).
  - `BackupRef.Sha256` = hash of the plaintext; `StoredSha256` = hash of the ciphertext (used by `verify` without decrypting).
  - `DiscRecord.KeyId` = the master key's id. `stage --append`, `restage`, `discjson` refuse a disc whose key id is
    not the catalog's; `restore` ignores such a mount; `scandisc`/`ReadManifest` explain it (`DiscFiles.OtherCatalogHint`).
  - Manifest: `manifest.jsonl.enc`; its lines carry `Source` (alias/relative path), the only link from hidden
    names to real paths besides the catalog. `DiscFiles.Write` refuses to write a plain manifest for an encrypted disc.
  - Lost catalog: `init --key-from <disc>` makes a new catalog with the disc's `bdxl.key` (same master key and
    catalog id, so every disc of that catalog opens), then `catalog backup restore <disc>_catalog --force` or
    `scandisc` each disc. After a password change, old discs' `bdxl.key` still opens with the old password.
  - No per-disc passwords any more (`BDXLENC1`, `KeyCache`, `KeyCheck`, `DiscPassword` are gone: nothing was ever
    staged with them).
- **Compression (`compression`):** `Settings.Compression` (default, `off`) + `Settings.CompressionByAlias`; modes
  off/auto/on + zstd level (ZstdSharp). Inside `BDXLENC2` each 1 MiB chunk is compressed (mode byte 1) only if smaller.
  - `scan` counts the exact stored size in the same read (`FileCrypto.HashAndMeasure`, same `Encode` as `Encrypt`):
    `FileRecord.CompressedSize` valid for `CompressedWith` = `Compression.Signature(level)` (level + library version).
    Unchanged files are read again only when that signature is outdated (`Totals.Counted`).
  - `MakePlan` uses `Compression.PlannedStoredSize`: the counted size if valid, else the uncompressed one (never overfills).
    Parts of split files are never compressed (their layout is in plain bytes).
  - `stage` compresses by today's setting and records the level on the copy (`BackupRef.Compression`, also in the
    manifest, so `scandisc` restores it); `restage` reuses that level, so a rebuilt copy has the same size.
  - `alias rename` carries the alias's setting along.
- **Catalog backups** (`_catalog` on every disc staged with `--with-catalog`, `catalog backup create`): the encrypted
  catalog files, `catalog.hash.json` and `bdxl.key` as they are, each with Reed-Solomon data, so creating, checking
  and repairing need no password. `backup restore` repairs, writes to `.restoring/`, checks with the key, then
  replaces; into its own catalog (same catalog id) the current `bdxl.key` stays, into an empty folder the backup's
  is used (password or `--recovery`), another catalog is refused.
- **Split files (`plan/stage --split`):** only files larger than every blank disc. Stored as `<path>.001`, `.002` ...
  (`.001.enc` when encrypted, each part on its own). A part is a `BackupRef` with `Part/Parts/Offset/Length/PartSha256`;
  `Sha256` stays the whole-file hash. `ContentSha256` = what the stored file holds (use it for verify/restage).
  - Covered = `Archive.CompleteCopy`: one whole copy OR every part of one layout on live discs. Never use
    "any backup ref" as "backed up".
  - Layout (part size = `FileCrypto.MaxPlainSize(largest usable blank)`) is fixed by the first staged part;
    `MakePlan` reuses it via `PartSets`, so only missing parts are pending (also without `--split`).
  - Planning works on `Piece`s (whole file or part); `Bin.Pieces`, `PlanResult.Pending` are pieces.
  - Parts are checked against size+mtime (like `scan`), not the whole hash; `restore` checks the joined whole file.
  - `restore` keeps verified parts as `<target>.bdxl-part.NNN` across runs (one drive) and joins when complete.
  - Manifest lines of parts carry part/parts/offset/length/fileSize/fileSha256, so `scandisc --method manifest`
    rebuilds them.
- **Appending (`stage --append <label>`, `plan --append`):** one more session on a burned, not finalized disc.
  - Folder `<dest>/<label>.sN/` with only the pending pieces that fit, their own manifest `manifest.sN.jsonl.enc`
    (`DiscFiles.EncryptedManifestFor`; only the session's files), `disc.json` (`sessions`), optional `_catalog`.
    Burned with a program that continues the file system, disc.json/_catalog replace the old ones; manifests add up.
    `DiscFiles.ReadManifest` reads every `manifest*.jsonl.enc` in session order, a later line replacing an earlier one
    with the same stored path (discs from before this, whose later manifest repeated the whole list, read the same).
  - Room: `DiscRecord.OccupiedBytes` = files + per session max(reserve, metadata). Free for files =
    capacity - occupied - reserve (`AppendTarget`); `--mount` checks disc.json's label and caps by the drive's
    free space when the drive reports any (open discs often show 0 in Windows, then the catalog figure is used).
  - Names are never reused: the session continues the disc's hidden numbering, so a newer version of a file gets a
    new name and both stay restorable. `ManifestLine.Source` is set whenever the stored name differs from
    alias/path(+part suffix).
  - `DiscRecord.PendingAppend` (`AppendSession`: counters before the session) until `burned` (dates only the
    copies with no `BackedUpUtc`) or `discard` (removes those copies, restores the counters). Only one pending
    session; `lost`/`destroyed`/`restage` refuse while one is pending. `restage` rebuilds all sessions as one folder.
  - Only encrypted discs with the catalog's key take a session (`AppendTarget.Open` refuses plain discs and other keys).
  - `scandisc` of such a disc: several versions of one path go into one catalog entry; for a file known only from
    the disc, the copy with the latest mtime becomes its current version.
- **Labels:** `Settings.LabelPrefix` + `LabelCounter` (`D4`). The counter only ever goes up (`Archive.NextLabel`).
  For old catalogs it is initialised from the existing `BDXL-nnnn` labels.
- **Blank discs:** `stage` uses up a `BlankDisc` and copies its capacity, reserve and cost to the `DiscRecord`.
  `discard` gives the blank disc back. Discs from `scandisc` have no reserve/occupied figures; appending to them needs
  `--capacity` at import or `--mount`.
- **Ignore rules:** `.gitignore` syntax, matched against `RelPath` (relative to the scanned folder).
  - `scan` evaluates each file it sees.
  - `ignore add/remove/apply` re-evaluates the whole catalog (`Archive.ApplyIgnoreRules`).
- **Scan safety:**
  - A folder that can't be read: files under it are not marked missing.
  - An unreachable location during a rescan of everything: skipped completely.
  - Moves: a file gone from its path whose hash turns up at a path with no copy of its own (new, or alias-only)
    is a move: the old record takes the new path, keeps its backups, the other record is dropped
    (`ScanCommand.DetectMoves`, after all locations, before `LinkDuplicates`).
  - Ctrl+C: stops walking/hashing (a file being hashed is dropped), saves, skips the "missing" step for the
    interrupted location and lists what was left; locations not reached are named.
  - Hashing: N worker threads each take the next file (no batches, so one large file never holds up the
    others); results are applied to the catalog on the main thread in the order they finish, the catalog is saved
    every 2 minutes, and new records are put back in folder-walk order at the end (same catalog as 1 thread).

## Tests (`tests/Bdxl.Tests`, xUnit, references `bdxl/Bdxl.csproj`)
Kept outside `bdxl/` on purpose: the app project compiles every `.cs` file below its folder.
`TestKeys` (TestUtil.cs): every test catalog uses one password with the cheapest Argon2id settings; a module
initializer sets `BDXL_PASSWORD` and `BDXL_AGENT=off`; `TestKeys.Init(dir)` / `TestKeys.Load(dir)` replace `init` / `Catalog.Load`.
| File | Covers |
|---|---|
| `GitIgnoreTests` | anchoring, `**`, `?`, classes, dir-only rules, negation, no re-include inside an ignored folder |
| `PackerTests` | best-fit order, nothing dropped, never overfilled, stored size via `sizeOf`, determinism |
| `SplitLayoutTests` | parts cover the file, suffixes, `PartSizeOf` from any part |
| `ArchiveTests` | `CompleteCopy` (layouts never mix), `DiscSpace` (sectors, entries, folder, manifest share never below the real line), small files planned with their overhead, old part layouts still fit, dedup, dead discs, `MakePlan` with/without `--split`, missing parts only, `LinkDuplicates`, `Matches`, labels |
| `FileCryptoTests` | `BDXLENC2` files: round trips around chunk boundaries, byte ranges, another key, flipped byte, truncation, cut-off chunks, `MaxPlainSize`, password policy |
| `ReedSolomonTests` | GF(256), repair of up to m blocks (data + parity), unrecoverable, truncated / missing files |
| `InterruptTests` | an interrupted scan saves, marks nothing missing, and a rerun finishes; interrupted stage/restage/restore/verify/scandisc leave nothing half-done (own non-parallel collection: `Interrupt` is process-wide) |
| `MasterKeyTests` | password/recovery envelopes, password change keeps the master key, envelopes bound to their catalog, `bdxl.key` round trip, KDF limits, `BDXLENC2` round trips/another key/another purpose/tampering, recovery key checksum; key rings: rotation, an old envelope opens only the old keys, a `bdxl-key-1` file opens and is rewritten as a ring |
| `AgentTests` | in-process server on its own pipe with a test clock: hand over / get / lock, idle time restarts with use, maximum, Win+L and sleep per catalog, other protocol, second agent refused, commands use the agent instead of the password |
| `CompressionTests` | compressed round trips (empty, text, one chunk, random, mixed) and `HashAndMeasure` = real stored size, damaged compressed chunk, auto/on/off, a location that fits only compressed: scan counts, plan, stage at the counted size, verify, restore, restage at the recorded level, level from the manifest; recount on a new level, alias rename, clear |
| `KeyRotationTests` | after a rotation: catalog under the new key, history still checks, old disc verified/restored via the ring, new disc on the new key, each disc's bdxl.key opens only its keys (old password: old key only), leak simulation with `init --key-from`, append + restage keep the disc's key, a broken catalog is not rotated |
| `CatalogTests` | nothing readable in an encrypted catalog, file list split per location (unchanged parts not rewritten, stale parts removed, rollback), unsplit list read and split + too-old marker, compression, damage, HMAC against mixed saves + accept, snapshots (never `bdxl.key`), pruning, rollback, `init` + wrong password, migration (history too, `--force`, interrupted), export/import, password change + recovery key, `password check`, backup restore into empty/own/other catalog |
| `OptionsTests`, `SizeTextTests` | argument parsing, flags, dates in UTC, sizes and money |
| `EndToEndTests` | real commands via `Program.Main`: stage/verify/burn/restore, corrupted disc, 1 vs 4 scan threads give the same catalog, moved files keep their record (also across separately scanned locations), encrypted split restore one drive at a time, lost + restage, hand-edited catalog |
| `EncryptedDiscTests` | nothing on an encrypted disc shows names/hashes, `bdxl.key` + README + key ids on the disc, verify + restore, a lost catalog rebuilt with `init --key-from` (wrong/no password refused), old and new discs across a password change, `_catalog` repaired without and restored with the password, standalone backup, restage, a disc of another key refused (discjson, restage, restore), discjson puts back manifest + key file |
| `AppendTests` | only new/changed files in the session, `name~2`, session manifest of its own (merged on the disc; an old full-list disc + new sessions), burn/verify/restore/scandisc of the merged disc, free space with reserve per session, discard of the session only, refusals, encrypted session (same master key, numbering continued, catalog rebuilt from the disc), a disc of another key refused (catalog or `--mount`) |
| `CopiesTests` | per-location copies on different discs, never two on one disc, staged copies count for planning but only burned ones as made, lost discs, higher number of two locations, not onto an `--append` disc that holds it, split parts twice, end to end (stage, burn, restore from either disc, lost + restaged, alias rename) |
| `MirrorTests` | an empty folder gets a copy that every save keeps in step (and opens as a catalog), without the mirror only read-only commands run (password change refused too), a missed save fixed by `mirror sync`, a damaged catalog taken back with `mirror restore` (also a listed-but-damaged file), a newer mirror, password change reaches the mirror, folder rules, `mirror none` while disconnected |

## Suggested next steps
Ideas not built yet:
   - `burn` command that runs an external burner from a configurable command template, then `verify` + `burned`.
   - Restore-time progress output.
   - Faster disc reads (`restore`, `verify`, `scandisc --method hash`). Do NOT read several files from the same
     optical disc in parallel: a CD/DVD/BD drive has one read head, so concurrent reads make it seek back and forth
     (slow seeks, spin-speed changes) and total throughput drops well below one sequential reader. The disc is the
     bottleneck (BDXL reads at roughly 18-36 MB/s; SHA-256 and AES-GCM are far faster), so instead:
     - one sequential reader per physical drive (parallel across different drives is fine);
     - pipeline per drive: read the next file / chunk while the previous one is hashed, decrypted and written
       (read → bounded queue → hash/decrypt/write), with large read buffers;
     - read in on-disc order (the order files were staged, roughly path order), not catalog order.
     `scan --threads` stays as it is: it reads from HDD/SSD sources, where parallel hashing does help.
