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
  plaintext catalogs, `password change`, `recovery-key`, `catalog export/import`, password-free catalog backups.
  Still to come: the agent (`unlock`/`lock`), then discs encrypted with the master key, encrypted-only, compression.

## Build / run
```
dotnet build -c Release
dotnet test ../BdxlPlanner.slnx -c Release   # from bdxl/; 173 tests, a few seconds
dotnet run -- help                 # all commands
dotnet run -- <command> --help     # details
dotnet run -- workflow             # sample session
```
Every command takes `--catalog <dir>` (default `./catalog`). Start with `init`; the password comes from `BDXL_PASSWORD`,
`--password-env VAR` or is typed (every command that loads the catalog needs it).

## Source layout
| Folder | Contents |
|---|---|
| `src/Program.cs` | `Main`: parse args → find command → load + integrity-check catalog → run. Exit codes: 0 ok, 1 error, 2 partial/not everything fits, 3 problems found, 4 catalog broken, 130 interrupted (Ctrl+C) |
| `src/Cli/Options.cs` | Arg parser. `Flags` set = options without a value (add new boolean options there!) |
| `src/Cli/ICommand.cs` | `ICommand`: `Name`, `Group` (a `CommandGroup` constant), `Summary`, `Usage`, `Details` (help text), `AllowBrokenCatalog` / `NeedsCatalog` (default false / true), `Run(Ctx)` |
| `src/Cli/CommandTable.cs` | The list of command instances; its order is the order of `bdxl help` |
| `src/Cli/Ui.cs` | `Confirm` (`--yes`), password prompt (`BDXL_PASSWORD` or `--password-env VAR`, else hidden typing) |
| `src/Cli/Interrupt.cs` | Ctrl+C: `Interruptible` commands get `Interrupt.Requested` and stop cleanly; others (or a 2nd Ctrl+C) exit at once with a note. `Catalog.Save` runs inside `Interrupt.Critical()`, so a save is never cut off |
| `src/Model/` | One file per type: `FileRecord`, `BackupRef`, `DiscRecord`, `BlankDisc`, `ManifestLine`, `DiscInfo`, `AppendSession`, `RepairMeta`, `RsStatus`, `Piece`, `Bin`, `PlanResult`, `DiscFile`, `UserException` |
| `src/Model/Settings.cs` | `settings.json`: label prefix + never-reset counter, currency, history-keep, default-threads, ignore rules |
| `src/Storage/Catalog.cs` | Load/Save (encrypted), integrity (`catalog.hash.json` + HMAC), snapshots in `history/`, rollback |
| `src/Storage/JsonStore.cs`, `Hashing.cs` | JSONL/JSON read+write (atomic via .tmp), SHA-256 helpers |
| `src/Core/` | `Packer` (best-fit-decreasing onto fixed blank discs), `GitIgnore`, `FileCrypto` (+`PasswordPolicy`, `KeyCache`; `BDXLENC1` per-disc password format and `BDXLENC2` master-key format), `MasterKey` (+`KeyPurpose`, HKDF object keys), `KeyFile` (`bdxl.key`, envelopes), `Kdf` (Argon2id via NSec/libsodium, calibration), `RecoveryKey` (text form with checksum), `ReedSolomon` (`Gf`, `Rs`), `SizeText` |
| `src/Services/` | `Ctx` (what every command gets; `Arg(i, usage)`), `Archive` (dedup, planning, labels, ignore application), `DiscFiles` (manifest + disc.json, encrypted manifest, hidden names, writing onto a mounted disc), `AppendTarget` (free space of a disc for `--append`), `DiscPassword` (checks the password of an existing encrypted disc), `CatalogSetup` (unlock, `init`, crash-safe migration of plaintext catalogs) |
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
- **Catalog files:** `files.jsonl.enc`, `discs.jsonl.enc`, `blanks.jsonl.enc`, `settings.json.enc` (`Catalog.StoredFiles`;
  plaintext names in `Catalog.CoveredFiles`), each `BDXLENC2` with purpose `catalog`, plus `bdxl.key`.
  `catalog.hash.json` = SHA-256 of each stored file + key id + save counter + HMAC (key from the master key).
  Every `Catalog.Save()` serializes in memory and encrypts straight into the files (plaintext never on disk).
- **Keys:** `Program.OpenCatalog`: no catalog = "run init"; plaintext catalog = migrate after confirmation;
  otherwise `CatalogSetup.Unlock` (env/typed, 3 tries). `Ctx.Key` is the master key; `Catalog.Key` saves with it.
  `bdxl.key` is never in `history/`; a password change rewrites only `bdxl.key`. `ICommand.NeedsCatalogFor(opt)`
  lets `catalog backup verify/restore` run without unlocking (restore unlocks the right key file itself).
- **Migration:** built in `.encrypting/`, plaintext moved to `.plaintext/`, encrypted files moved in, `bdxl.key` last.
  No `bdxl.key` = undo (`RecoverInterruptedMigration`); leftovers + plaintext history snapshots are finished by
  `FinishMigration` (snapshots that failed their old hash check get `unverified`).
- **Integrity states:** `Fresh` (empty), `Ok`, `Broken` (only commands with `allowBrokenCatalog` run; `Save()` throws).
  Damage shows without the key (`CheckIntegrity(dir, null)`); mixed/altered files fail the HMAC.
- **Versioning:** the first `Save()` in a process copies the on-disk catalog plus its hash file to
  `history/<yyyyMMdd-HHmmss>-<command>/`. That is one copy per command, pruned to `HistoryKeep`.
- **Dedup:** content identity is `FileRecord.Sha256`, the hash of the plaintext.
  - Pending = still on the source, not ignored, already hashed, and no live disc holds that hash (`Archive.MakePlan`).
  - Duplicates get a `BackupRef` with `Alias = true` that points at the stored copy (`Archive.LinkDuplicates`).
  - "Live" disc = state is not `lost` or `destroyed`.
- **Ignored files:** `Sha256 = ""` (never hashed), `Ignored = true`. Any code that uses the hash must skip empty hashes.
  `restage` ignores the `Ignored` flag on purpose.
- **Encryption (`stage --encrypt`):** nothing but `disc.json` is readable without the password.
  - Stored path = hidden name `data/NNN/NNNNNN.enc` (`DiscFiles.HiddenName`, numbered in staging order, 1000 per
    folder; no source mtime). Discs staged before this keep `<alias>/<rel>.enc`; both work, since everything
    uses `BackupRef.DiscPath`.
  - `BackupRef.Sha256` = hash of the plaintext; `StoredSha256` = hash of the ciphertext (used by `verify` without a password).
  - Format `BDXLENC1`: header (iterations, salt, nonce prefix), then 1 MiB AES-GCM chunks; the AAD is the chunk number plus a last-chunk flag.
    `FileCrypto.EncryptBytes/DecryptBytes` do the same in memory (metadata never touches a disk in plaintext).
  - One salt per staged disc; keys are cached per salt. The manifest uses the disc's password and salt.
    (Phase 2 replaces all of this with the catalog's master key.)
  - Manifest: `manifest.jsonl.enc`; its lines carry `Source` (alias/relative path), the only link from hidden
    names to real paths besides the catalog. `DiscFiles.Write` refuses to write a plain manifest for an encrypted
    disc. Readers: `DiscFiles.ReadManifest` (asks for the password). `restage`/`discjson` of an encrypted disc
    therefore always need the password.
  - Password check: `DiscRecord.KeyCheck` = `FileCrypto.NewKeyCheck` ("iterations:salt:check", its own salt, as
    costly to brute-force as any .enc file). Set by `stage --encrypt` and by `scandisc` when a password opens the
    manifest. `DiscPassword.Get` (append, restage, discjson) checks against it; without one it checks against an
    encrypted file of the disc (`FileCrypto.OpensWith`, first chunk only) and records it. So a disc never gets a
    second password.
  - `disc.json` stays plain (`restore` identifies discs before any password is known) and has `"encrypted": true`.
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
  - Folder `<dest>/<label>.sN/` with only the pending pieces that fit, a manifest of the WHOLE disc (old lines are
    built from the catalog: `BackupRef.StoredSize`), `disc.json` (`sessions`), optional `_catalog`. Burned with a
    program that continues the file system, the new manifest/disc.json/_catalog replace the old ones.
  - Room: `DiscRecord.OccupiedBytes` = files + per session max(reserve, metadata). Free for files =
    capacity - occupied - reserve (`AppendTarget`); `--mount` checks disc.json's label and caps by the drive's
    free space when the drive reports any (open discs often show 0 in Windows, then the catalog figure is used).
  - Names are never reused: a newer version of a file already on a plain disc is stored as `name~2.ext`
    (`StageCommand.FreeName`) and its manifest line gets `Source`; encrypted discs continue the hidden numbering.
    `ManifestLine.Source` is set whenever the stored name differs from alias/path(+part suffix).
  - `DiscRecord.PendingAppend` (`AppendSession`: counters before the session) until `burned` (dates only the
    copies with no `BackedUpUtc`) or `discard` (removes those copies, restores the counters). Only one pending
    session; `lost`/`destroyed`/`restage` refuse while one is pending. `restage` rebuilds all sessions as one folder.
  - The disc's encryption is kept (`--encrypt` on a plain disc is an error); the password is checked (`DiscPassword`).
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
| `ArchiveTests` | `CompleteCopy` (layouts never mix), dedup, dead discs, `MakePlan` with/without `--split`, missing parts only, `LinkDuplicates`, `Matches`, labels |
| `FileCryptoTests` | round trips around chunk boundaries, byte ranges, wrong password, flipped byte, truncation, cut-off chunks, `MaxPlainSize`, password policy, key check, `OpensWith` |
| `ReedSolomonTests` | GF(256), repair of up to m blocks (data + parity), unrecoverable, truncated / missing files |
| `InterruptTests` | an interrupted scan saves, marks nothing missing, and a rerun finishes; interrupted stage/restage/restore/verify/scandisc leave nothing half-done (own non-parallel collection: `Interrupt` is process-wide) |
| `MasterKeyTests` | password/recovery envelopes, password change keeps the master key, envelopes bound to their catalog, `bdxl.key` round trip, KDF limits, `BDXLENC2` round trips/another key/another purpose/tampering, recovery key checksum |
| `CatalogTests` | nothing readable in an encrypted catalog, damage, HMAC against mixed saves + accept, snapshots (never `bdxl.key`), pruning, rollback, `init` + wrong password, migration (history too, `--force`, interrupted), export/import, password change + recovery key, backup restore into empty/own/other catalog |
| `OptionsTests`, `SizeTextTests` | argument parsing, flags, dates in UTC, sizes and money |
| `EndToEndTests` | real commands via `Program.Main`: stage/verify/burn/restore, corrupted disc, 1 vs 4 scan threads give the same catalog, moved files keep their record (also across separately scanned locations), encrypted split restore one drive at a time, lost + restage, hand-edited catalog |
| `EncryptedDiscTests` | nothing on an encrypted disc shows names/hashes, verify + restore, `scandisc` rebuild from the encrypted manifest (wrong/no password), `_catalog` repaired without and restored with the password, standalone `--encrypt` backup, restage, discjson, a different password refused |
| `AppendTests` | only new/changed files in the session, `name~2`, whole-disc manifest, burn/verify/restore/scandisc of the merged disc, free space with reserve per session, discard of the session only, refusals, encrypted session (password checked, numbering continued), password check against the disc via `--mount` |

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
