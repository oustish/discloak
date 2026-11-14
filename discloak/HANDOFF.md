# Discloak – handoff notes (state as of 2026-11-04, version 0.9.1; called bdxl before 0.8.0)

How to build, test, version and commit: [`../CONTRIBUTING.md`](../CONTRIBUTING.md). Changes per version:
[`../CHANGELOG.md`](../CHANGELOG.md). License: PolyForm Shield 1.0.0 ([`../LICENSE.md`](../LICENSE.md),
[`../LICENSING.md`](../LICENSING.md): plain words, name and format rules; [`../THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md)),
copied next to `discloak.exe` by publish (`Discloak.csproj`); `BuildInfo.Copyright`/`License` for `version`.

## Status
- **Repository (0.9.1):** root `README.md`, `SECURITY.md`, `.gitattributes` (LF; `*.ps1` CRLF; the fixtures
  untouched), and `tools/release.ps1 <tag>`: builds the tag in a temporary worktree on a branch `release/<tag>` (so
  the exe is clean and names it), checks the exe's version, and writes `release/<tag>/` (ignored): the win-x64 zip,
  the source zip (`git archive`), `notes.md` (the changelog section); no checksum file (GitHub shows the SHA-256). `tools/publish-wiki.ps1`
  copies `docs/*.md` into the wiki repo (`discloak.wiki.git`): page links without `.md`, other links to
  `github.com/oustish/discloak/blob|tree/master/...`, a `_Footer.md`; refuses uncommitted docs or an unpushed commit.
  Release steps: CONTRIBUTING.
- **The program on every disc (0.9.0):** `DiscProgram` writes `_program\` (the running `discloak.exe` when it is the
  published single file - no `discloak.dll` next to it -, the source zip and the license files, both embedded as
  resources: `DiscloakSourceZip` target and `EmbeddedResource` in `Discloak.csproj`). `Archive.MakePlan(programSpace:)`
  takes `DiscProgram.SpaceFor(opt)` off every disc's room (stage, append, plan, status); stage counts the written
  folder (`SpaceOf`) as occupied; restage adds it only if `capacity - packs - manifest - reserve` leaves room; verify
  checks it. `--without-program` everywhere. The publish profile is self-contained and compressed (about 37 MB);
  `THIRD-PARTY-NOTICES-dotnet.txt` is the runtime's own notices. Tests use small stand-ins
  (`TestKeys.ProgramFiles`, `DiscProgram.UseFilesForTests`). A save converts every part of a catalog still in
  `BDXLENC2` (`Catalog.ReEncryptLegacy` for parts it did not read, `FileCrypto.IsLegacy`).
- **Renamed from bdxl to Discloak (0.8.0, 2026-11-04):** command, exe, assembly, namespaces (`Discloak`,
  `Discloak.Tests`), folders (`discloak/`, `tests/Discloak.Tests/`, `Discloak.slnx`), environment variables
  (`DISCLOAK_*`, the old `BDXL_*` are not read any more), the agent's folder and pipe, the default label prefix
  (`DISC-`). Data written before keeps the old names and is read as the same formats - "read both, write new":
  `DiscFormat.LegacyName`/`LegacyMagic` (`bdxl-pack-1`, `BDXLPAK1`), `FileCrypto.LegacyMagic` (`BDXLENC2`),
  `KeyFile.LegacyFileName`/`LegacyFormat2`/`Format1` (`bdxl.key`, `bdxl-key-2`, `bdxl-key-1`). `KeyFile.PathIn` finds
  the key file under either name (`discloak.key` first), `NewPathIn` is where it is written; `MigrateName` renames a
  catalog's (on save) and a mirror's (on sync). The labels inside the cryptography keep the old name and must never
  change: `"bdxl-enc2 "` + purpose (`MasterKey.ObjectKey`), `"bdxl catalog integrity"` (`IntegrityKey`),
  `"bdxl-catalog-integrity-1"` (`IntegrityManifest.ComputeMac`), `"bdxl recovery key"`, the envelope AAD labels
  (`KeyFile.RingAadLabel`/`SingleKeyAadLabel`), and the old-version marker in `files.jsonl.enc`. Proof: the fixture
  `tests/Discloak.Tests/Fixtures/bdxl-0.7.1/` (written by bdxl 0.7.1 itself) and `LegacyNamesTests`.
- C# / .NET 10 console app (`net10.0`), 86 source files under `src/`, single namespace `Discloak`, classic `Main` (no top-level statements).
- Builds cleanly (0 warnings). Smoke-tested by hand on 2026-10-29 in a temp folder: every command was run, including
  encryption, tamper detection, lost/restage, catalog rollback, backup repair and scandisc. Automated tests: `tests/Discloak.Tests` (xUnit, see below).
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
  `verify` says what it checks, shows a status line (pack, progress, speed, time left, the file being read), prints every
  file with `--deep` and a damaged pack as soon as it is read. Before the packs it checks the disc's other files:
  disc.json, packs.sha256 (= `DiscRecord.Packs`), the manifests (SHA-256 = `DiscRecord.Manifests`, one per session,
  recorded by stage/restage/scandisc; `--deep` decrypts them and compares their lines with `CopiesOn`, and records the
  hashes of a disc staged before they were kept), discloak.key, README.txt and `_catalog` (`CatalogCommand.Load`).
  `--deep` prints each checked file as `  OK       name (size)` / `  DAMAGED  name (size): why`, lined up with the disc
  files' lines (no status in front). `--packs` reads only the packs given (`PackReader.ReadPack`; with `--deep` files
  wholly in them are hash-checked, the others only have their part decrypted). `--mark-burned` calls
  `BurnedCommand.Mark` after a clean full verify (the same save). After damage, `Advise` prints what to do: burn the
  packs again from the staging folder (disc not marked as burned yet) or restore/restage. `RecordDamage` marks the
  copies found damaged (`BackupRef.DamagedUtc`/`Damage`; without --deep every copy with data in a bad pack, with it
  only the files that failed) and clears the mark, with a WARNING, from a marked copy read back intact as a whole
  (all its packs OK, or --deep checked it whole). A marked copy does not count (`Archive.Counts`: LiveCopies,
  HasLiveCopyOfCurrent, HasAnyLiveCopy, BurnedCopies, TooFewCopies, status) - the file is pending again - but stays
  in the catalog: restore tries it last, `Holds` (append) still sees it on its disc, restage clears it.
  `stage`, `restore` and `restage` show progress the same way (`ProgressMeter`).
- **Versions (0.6.0, 2026-11-03):** `<Version>` in `Discloak.csproj`, raised by hand (CONTRIBUTING.md). The
  `DiscloakBuildInfo` target runs git at build time: InformationalVersion `0.6.0+<commit>[-dirty].<yyyyMMddTHHmmZ>` and
  assembly metadata GitCommit/GitBranch/GitDirty/BuiltUtc, read by `BuildInfo` (`src/Cli/BuildInfo.cs`); `version`
  / `--version` prints them. Every staged session records `BuildInfo.CreatedBy`: `disc.json` "createdBy" (latest
  session) and `DiscRecord.WrittenBy` (per session, kept with `Manifests` by `SetSession`/`DropSessionsFrom`;
  restage and scandisc set it too). Compressed copies record `BackupRef.Codec` / manifest "codec" = "zstd"
  (`Compression.Zstd`; none = zstd); `Compression.Unreadable` refuses another codec with a reason (`StoredForm.Read`,
  restage).
- **Encrypted catalog (phase 1 of `ENCRYPTION.md`, the agreed design):** the catalog is encrypted with a random master
  key that `discloak.key` holds wrapped by the password (Argon2id) and optionally a recovery key; `init`, migration of
  plaintext catalogs, `password change`, `password check` (one password against any discloak.key, no agent), `recovery-key`, `catalog export/import`, password-free catalog backups.
  `unlock` hands the master key to a background agent (memory only, idle/max timeouts, Win+L/sleep), `lock` takes it back.
  Encrypted discs use the same master key and carry `discloak.key` + `README.txt` (phase 2); `init --key-from <disc>`.
  Every staged disc is encrypted (phase 3). Compression per location (phase 4, `compression`) completes the design.
  Key rotation (`password change --rotate-key`, a `KeyRing` in `discloak.key` format `bdxl-key-2`) came after it.
- **Discs in packs (phase 6, 2026-11-02):** a disc's files are laid end to end in one stream, cut into equal
  64 MiB packs, each encrypted on its own; the manifest is padded to whole 64 MiB, `disc.json` holds nothing about the
  files. Nothing on a disc shows names, sizes or count. Plain discs (written without discloak) and `discjson` are gone;
  `scandisc` reads only discloak discs. See `ENCRYPTION.md` › Discs in packs.

## Build / run
```
dotnet build -c Release
dotnet publish -p:PublishProfile=win-x64   # single-file ../publish/discloak.exe (Properties/PublishProfiles/win-x64.pubxml)
dotnet test ../Discloak.slnx -c Release   # from discloak/; 253 tests, a few seconds
dotnet run -- help                 # all commands
dotnet run -- <command> --help     # details
dotnet run -- workflow             # sample session
```
Every command takes `--catalog <dir>` (default `./catalog`). Start with `init`; the password comes from `DISCLOAK_PASSWORD`,
`--password-env VAR` or is typed (every command that loads the catalog needs it). After `unlock` the agent provides it.

## Source layout
| Folder | Contents |
|---|---|
| `src/Program.cs` | `Main`: parse args → find command → load + integrity-check catalog → run. Exit codes: 0 ok, 1 error, 2 partial/not everything fits, 3 problems found, 4 catalog broken, 130 interrupted (Ctrl+C) |
| `src/Cli/Options.cs` | Arg parser. `Flags` set = options without a value (add new boolean options there!) |
| `src/Cli/ICommand.cs` | `ICommand`: `Name`, `Group` (a `CommandGroup` constant), `Summary`, `Usage`, `Details` (help text), `AllowBrokenCatalog` / `NeedsCatalog` (default false / true), `Run(Ctx)` |
| `src/Cli/CommandTable.cs` | The list of command instances; its order is the order of `discloak help` |
| `src/Cli/Ui.cs` | `Confirm` (`--yes`), password prompt (`DISCLOAK_PASSWORD` or `--password-env VAR`, else hidden typing); a new password: typed, then copied back from the password manager, then confirmed as saved |
| `src/Cli/StatusLine.cs` | The status line rewritten in place at the bottom of the console (nothing when output is redirected); `Rate` ("12.4%  85.3 MB/s  ~3h 12m left"), `Count`, `Percent`, `Duration`. `ProgressMeter`: a job's progress by bytes (`Total`, `Done`, `Show` at most once a second, `WriteLine` for lines that stay). Used by `scan`, `verify`, `stage`, `restore`, `restage` |
| `src/Cli/TextTable.cs` | A console table: every column as wide as its widest cell (`cost report`) |
| `src/Cli/BuildInfo.cs` | The build's version, configuration, commit, branch, dirty flag and build time (from the assembly attributes the `DiscloakBuildInfo` target in `Discloak.csproj` writes); `CreatedBy` = what a staged session records |
| `src/Cli/Interrupt.cs` | Ctrl+C: `Interruptible` commands get `Interrupt.Requested` and stop cleanly; others (or a 2nd Ctrl+C) exit at once with a note. `Catalog.Save` runs inside `Interrupt.Critical()`, so a save is never cut off |
| `src/Model/` | One file per type: `FileRecord`, `BackupRef`, `DiscRecord`, `BlankDisc`, `ManifestLine`, `DiscInfo`, `AppendSession`, `RepairMeta`, `RsStatus`, `Piece`, `Bin`, `PlanResult`, `UserException` |
| `src/Model/Settings.cs` | `settings.json`: label prefix + never-reset counter, currency, history-keep, default-threads, ignore rules |
| `src/Storage/Catalog.cs` | Load/Save (encrypted), integrity (`catalog.hash.json` + HMAC), snapshots in `history/`, rollback |
| `src/Storage/JsonStore.cs`, `Hashing.cs` | JSONL/JSON read+write (atomic via .tmp), SHA-256 helpers |
| `src/Storage/SecureDelete.cs` | `catalog import` wiping the plain export: `Overwrite` (random bytes, write-through), `Erase` (overwrite, random name, length 0, delete), `Folder`, `Unsafe` (subfolders, links, a drive root) |
| `src/Storage/DiscRead.cs` | `verify --no-cache`: `UncachedFile` reads past the Windows file cache (FILE_FLAG_NO_BUFFERING): aligned blocks (1 MiB, or the repair block size for `_catalog`) into pinned, 4 KiB-aligned memory, served from there; `DiscRead.Open`/`AllBytes` pick it or a normal FileStream. verify uses it for `--no-cache`, and every retry of a pack read (verify, restore) goes through it |
| `src/Core/` | `DiscFormat` (pack geometry, paths, `Room`), `Packs.cs` (`PackWriter`: the data stream into packs; `PackReader`: ranges back, chunk by chunk, or whole packs for `verify --deep`), `StoredForm` (a file as it is or as zstd frames; `HashAndMeasure` for scan), `DiscSpace` (sectors, manifest share), `Packer` (best-fit-decreasing onto fixed blank discs), `GitIgnore`, `FileCrypto` (`BDXLENC2` for catalog files and manifests, +`PasswordPolicy`), `MasterKey` (+`KeyPurpose`, HKDF object keys), `KeyRing` (every master key so far, oldest first; current = last), `KeyFile` (`discloak.key`, envelopes), `Kdf` (Argon2id via NSec/libsodium, calibration), `RecoveryKey` (text form with checksum), `Compression` (+`CompressionSetting`: per-alias mode/level, auto's extension list, signatures, planned sizes), `ReedSolomon` (`Gf`, `Rs`), `SizeText` |
| `src/Services/` | `Ctx` (what every command gets; `Arg(i, usage)`), `Archive` (dedup, planning, labels, ignore application), `DiscFiles` (a disc's metadata: padded encrypted manifest, `packs.sha256`, disc.json, discloak.key, README.txt; reading manifests and pack lists), `AppendTarget` (free space of a disc for `--append`), `CatalogSetup` (unlock, `init`, crash-safe migration of plaintext catalogs) |
| `src/Agent/` | `AgentServer` (keys per catalog id, timeouts, Win+L/sleep via `SystemEvents`, sweep every 5 s: a late sweep = the computer slept), `AgentClient`, `AgentProtocol` (length-prefixed JSON over a `CurrentUserOnly` named pipe, protocol version, pipe name per user or `DISCLOAK_AGENT_PIPE`), `AgentLauncher` (copies the program to `%LOCALAPPDATA%\discloak\agent\<build>`, starts it detached and outside the terminal's job) |
| `src/Commands/` | One file per command: `<Name>Command.cs`, a sealed class implementing `ICommand` with its own help text |

## Adding a command
1. `src/Commands/FooCommand.cs`: `public sealed class FooCommand : ICommand` with Name/Group/Summary/Usage/Details and `Run`.
2. Add `new FooCommand()` to `CommandTable.All` where it should appear in `discloak help`.
3. New boolean options go into `Options.Flags`.
4. A long-running command that can stop cleanly: `Interruptible => true`, check `Interrupt.Requested` at safe points,
   save, say what was left undone and return `Interrupt.ExitCode`. Long copies/hashes/decryptions take
   `Interrupt.Token` (checked every 1 MiB, throws OperationCanceledException). Interruptible now: scan, stage,
   restage, restore, verify, scandisc. On Ctrl+C: `stage` finishes the current disc/session with what was copied;
   `restage` keeps `<label>.partial` (reuse with --from), catalog unchanged; `restore` stops after the file;
   `verify` reports what was checked (and records the damage found so far); `scandisc` records nothing.

Shared pieces: `lost` and `destroyed` both call `MarkDeadDisc.Run`; `stage --with-catalog` calls `CatalogCommand.WriteBackup`;
`stage`/`restage` write `DiscFiles.Write`.
`Archive.CopiesOn(label)` = one (file, copy) per position (`At`) of a disc, in stream order, preferring the file it was
stored for over aliases.

## Key rules / invariants
- **Catalog files:** `files.NNN.jsonl.enc` (the file list, `FilesPerShard` = 200k records per part, a part never
  spans two locations), `discs.jsonl.enc`, `blanks.jsonl.enc`, `settings.json.enc`, each `DCLKENC2` (`BDXLENC2` before 0.8.0) with purpose
  `catalog` and zstd level 3 (`Catalog.CompressionLevel`), plus `discloak.key`. Always list them with
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
  explicit sync/restore hash the real files (`CopyCatalog(trustTarget: false)`). Commands that write `discloak.key` or
  restore files without `Save` call `Mirror.Sync` themselves (password change after `RequireInSync`, recovery-key,
  rollback, backup restore). The mirror never gets history or `mirror.json`.
- **Keys:** `Program.OpenCatalog`: no catalog = "run init"; plaintext catalog = migrate after confirmation;
  otherwise `CatalogSetup.Unlock` (env/typed, 3 tries). `Ctx.Key` is the master key; `Catalog.Key` saves with it.
  `discloak.key` is never in `history/`; a password change rewrites only `discloak.key`. `ICommand.NeedsCatalogFor(opt)`
  lets `catalog backup verify/restore` run without unlocking (restore unlocks the right key file itself).
- **Key ring:** `Catalog.Keys` / `Ctx.Keys` is the `KeyRing`; `Ctx.Key` = its current key (everything new is encrypted
  with it). Decryption finds the key by id (`FileCrypto.Decrypt(..., KeyRing)`, `CheckIntegrity` via the key id in
  `catalog.hash.json`). Discs: `c.Keys.For(disc.KeyId)` for `--append` and `restage`; `Contains` for checks.
  `password change --rotate-key` adds a key, replaces the recovery key, writes `discloak.key` first, then re-saves the
  catalog and locks the agent (protocol 2 carries the ring; a stale ring is ignored: its current key id differs).
- **Agent:** `CatalogSetup.Unlock` asks the agent first (`DISCLOAK_AGENT=off` skips it; the tests set that). The
  agent (hidden command `agent`) refuses to start if its pipe exists, keeps one listening pipe instance at all times,
  exits when its last key is locked (or if no key arrives within a minute), logs to `%LOCALAPPDATA%\discloak\agent\agent.log`
  without key material. `unlock` replaces an agent of another protocol version. `lock` needs no password.
- **Migration:** built in `.encrypting/`, plaintext moved to `.plaintext/`, encrypted files moved in, `discloak.key` last.
  No `discloak.key` = undo (`RecoverInterruptedMigration`); leftovers + plaintext history snapshots are finished by
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
- **Discs (packs; every staged disc is encrypted, `--encrypt` is accepted and ignored):** see `ENCRYPTION.md` › Discs
  in packs. Readable without the password: `disc.json` (label, date, sessions, format, geometry, `catalogId`/`keyId`
  - nothing about the files), `packs.sha256`, `discloak.key` (copied from the catalog at staging: the password of that day)
  and `README.txt` (`DiscFiles.Readme`: enough to write a decryptor).
  - A copy = `BackupRef` (`Disc`, `At` = position in the disc's data stream, `StoredSize`, `Length` = plain bytes,
    `Compression`, `Sha256` = the whole file's plaintext hash, part fields). There are no per-file names on a disc.
  - `PackWriter` (stage, restage) writes the stream at `Position`: a chunk is encrypted when full, a pack closed and
    hashed when full; `Finish` fills the last pack with zeros. Each pack gets a new random salt, so a rewritten pack
    never reuses a key/nonce. `DiscRecord.Packs` = SHA-256 of each pack (index = number - 1).
  - `stage` writes each piece with `StoredForm.Write(..., limit: planned size)`: a file that changed is left out (its
    bytes stay as unused space in the pack: never rewind, never re-encrypt with the same nonce).
  - `PackReader` reads a range chunk by chunk (restore); `wholePacks` (verify --deep) reads each pack once in full,
    hashes it, decrypts all its chunks, and reports it via `PackRead`. A damaged/unreadable chunk throws
    `InvalidDataException` only for ranges that touch it. Retries: a header or chunk that cannot be read or does not
    decrypt is read again `Retries` times (default 2) through `OpenFile(path, noCache: true)` - a fresh handle past the
    Windows cache - and the hash takes the last attempt's bytes; `Reread` reports a read that came right late
    (`RE-READ` in verify and restore). `HashPack` does the same for a plain verify (whole pack, hash compared).
    `OpenFile` is the seam the tests use to fail reads on purpose.
  - `DiscRecord.KeyId` = the master key's id. `stage --append` and `restage` refuse a disc whose key id is not the
    catalog's; `restore` ignores such a mount; `scandisc`/`ReadManifest` explain it (`DiscFiles.OtherCatalogHint`).
  - Manifest: `manifest.enc` / `manifest.sN.enc`, lines = `ManifestLine` (source, at, size, plainSize, sha256,
    modified, compression, part fields), padded with empty lines to whole `PackData` (`DiscFiles.SealManifest`).
  - `DiscInfo.Unreadable()`: a reader refuses a disc of another format or geometry. Tests use 8 × 8 KiB packs
    (`DiscFormat.UseGeometryForTests` in the test module initializer).
  - Lost catalog: `init --key-from <disc>` makes a new catalog with the disc's `discloak.key` (same master key and
    catalog id, so every disc of that catalog opens), then `catalog backup restore <disc>_catalog --force` or
    `scandisc` each disc. After a password change, old discs' `discloak.key` still opens with the old password.
  - No per-disc passwords any more (`BDXLENC1`, `KeyCache`, `KeyCheck`, `DiscPassword` are gone: nothing was ever
    staged with them).
- **Planning sizes:** a piece takes `Archive.StoredSizeOf` + `DiscSpace.ManifestShare`; a disc's room is
  `DiscFormat.Room(usable)` (less one pack, one manifest bucket and small files, scaled by pack-on-disc / pack data),
  passed to `Packer.Allocate` as `roomOf` (`Bin.Usable`).
- **Compression (`compression`):** `Settings.Compression` (default, `off`) + `Settings.CompressionByAlias`; modes
  off/auto/on + zstd level (ZstdSharp). On discs: one zstd frame per 1 MiB of the file (`StoredForm`).
  - `scan` counts the exact stored size in the same read (`StoredForm.HashAndMeasure`, same frames as `Write`):
    `FileRecord.CompressedSize` valid for `CompressedWith` = `Compression.Signature(level)` (stored form + level + library version).
    Unchanged files are read again only when that signature is outdated (`Totals.Counted`).
  - `Compression.StoredLevel`: today's level only if counted for it, else 0 - so `PlannedStoredSize` and `stage` agree
    and nothing overfills. Parts of split files are never compressed (their layout is in plain bytes).
  - `stage` records the level on the copy (`BackupRef.Compression`, also in the manifest, so `scandisc` restores it);
    `restage` reuses that level, so a rebuilt copy has the same bytes.
  - `alias rename` carries the alias's setting along.
- **Catalog backups** (`_catalog` on every disc staged with `--with-catalog`, `catalog backup create`): the encrypted
  catalog files, `catalog.hash.json` and `discloak.key` as they are, each with Reed-Solomon data, so creating, checking
  and repairing need no password. `backup restore` repairs, writes to `.restoring/`, checks with the key, then
  replaces; into its own catalog (same catalog id) the current `discloak.key` stays, into an empty folder the backup's
  is used (password or `--recovery`), another catalog is refused.
- **Split files (`plan/stage --split`):** only files larger than every blank disc. A part is a `BackupRef` with
  `Part/Parts/Offset/Length/PartSha256` (a range of the source file, stored as it is in the disc's stream);
  `Sha256` stays the whole-file hash. `ContentSha256` = what the stored copy holds (use it for verify/restage).
  - Covered = `Archive.CompleteCopy`: one whole copy OR every part of one layout on live discs. Never use
    "any backup ref" as "backed up".
  - Layout (part size = `DiscFormat.Room(largest usable blank)` - the part's manifest line) is fixed by the first staged part;
    `MakePlan` reuses it via `PartSets`, so only missing parts are pending (also without `--split`).
  - Planning works on `Piece`s (whole file or part); `Bin.Pieces`, `PlanResult.Pending` are pieces.
  - Parts are checked against size+mtime (like `scan`), not the whole hash; `restore` checks the joined whole file.
  - `restore` keeps verified parts as `<target>.discloak-part.NNN` across runs (one drive) and joins when complete.
  - Manifest lines of parts carry part/parts/offset/fileSize/fileSha256, so `scandisc` rebuilds them.
- **Appending (`stage --append <label>`, `plan --append`):** one more session on a burned, not finalized disc.
  - Folder `<dest>/<label>.sN/` with only the pending pieces that fit, in packs that continue the disc's numbering
    (the stream goes on at the next pack boundary), their own manifest `manifest.sN.enc` (`DiscFiles.ManifestFor`;
    only the session's files), `packs.sha256` (every pack of the disc), `disc.json` (`sessions`), optional `_catalog`.
    Burned with a program that continues the file system, packs.sha256/disc.json/_catalog replace the old ones;
    packs and manifests add up. `DiscFiles.ReadManifest` reads every manifest in session order (a later line for the
    same `at` replaces an earlier one).
  - Room: `DiscRecord.OccupiedBytes` = packs + manifest + per session max(reserve, metadata). Free =
    capacity - occupied - reserve (`AppendTarget`), turned into room by `DiscFormat.Room`; `--mount` checks
    disc.json's label (and format) and caps by the drive's free space when the drive reports any (open discs often
    show 0 in Windows, then the catalog figure is used).
  - A newer version of a file on the disc is stored again at a new position; both stay restorable.
  - `DiscRecord.PendingAppend` (`AppendSession`: counters and pack count before the session) until `burned` (dates
    only the copies with no `BackedUpUtc`) or `discard` (removes those copies and packs, restores the counters). Only
    one pending session; `lost`/`destroyed`/`restage` refuse while one is pending. `restage` rebuilds all sessions as
    one folder.
  - Only discs with a key of this catalog take a session (`AppendTarget.Open`).
  - `scandisc` of such a disc: several versions of one path go into one catalog entry; for a file known only from
    the disc, the copy with the latest `modified` becomes its current version.
- **Restage** keeps the layout (every `At` stays): per pack, copy from `--from` if its hash matches `DiscRecord.Packs`
  (or the folder's `restage.json` from an interrupted restage of this label), else rebuild the pack from source
  files (`RestageCommand.PieceSource` regenerates a piece's stored bytes - same compression level, same frames - and
  checks its hash when the piece ends). `restage.json` lists only packs whose pieces are all checked. Failed pieces
  abort (exit 3) unless `--allow-partial` (their copies are removed; their bytes stay as unused space).
- **Labels:** `Settings.LabelPrefix` + `LabelCounter` (`D4`), one counter for all prefixes (`Archive.NextLabel`). It
  goes back only in `discard` (`Archive.ReclaimLabels`): to the highest number at the end of any label still in the
  catalog (`HighestLabelNumber`), so the next disc takes the number after it; free numbers below are not reused, lost
  and destroyed discs keep theirs. A catalog from before the counter starts from `HighestLabelNumber` too.
- **Blank discs:** `stage` uses up a `BlankDisc` and copies its capacity, reserve and cost to the `DiscRecord`.
  `discard` gives the blank disc back. Discs from `scandisc` have no reserve figure (occupied = the disc folder's
  space); appending to them needs `--capacity` at import or `--mount`.
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

## Tests (`tests/Discloak.Tests`, xUnit, references `discloak/Discloak.csproj`)
Kept outside `discloak/` on purpose: the app project compiles every `.cs` file below its folder.
`TestKeys` (TestUtil.cs): every test catalog uses one password with the cheapest Argon2id settings; a module
initializer sets `DISCLOAK_PASSWORD` and `DISCLOAK_AGENT=off` and makes packs small (8 × 8 KiB); `TestKeys.Init(dir)` /
`TestKeys.Load(dir)` replace `init` / `Catalog.Load`; `TestKeys.Packs(disc)` lists a disc folder's packs.
| File | Covers |
|---|---|
| `GitIgnoreTests` | anchoring, `**`, `?`, classes, dir-only rules, negation, no re-include inside an ignored folder |
| `PackerTests` | best-fit order, nothing dropped, never overfilled, stored size via `sizeOf`, determinism |
| `SplitLayoutTests` | parts cover the file, suffixes, `PartSizeOf` from any part |
| `ArchiveTests` | `CompleteCopy` (layouts never mix), `DiscSpace` (sectors, entries, folder, manifest share never below the real line), small files take only their bytes, a disc's room filled exactly, dedup, dead discs, `MakePlan` with/without `--split`, missing parts only, `LinkDuplicates`, `Matches`, labels, labels after the highest number of any prefix and given back by `ReclaimLabels` (never below a disc still in the catalog) |
| `FileCryptoTests` | `DCLKENC2` streams: round trips around chunk boundaries, byte ranges, another key, flipped byte, truncation, cut-off chunks, password policy |
| `PackTests` | real geometry (64 MiB, 67,109,944-byte packs), equal pack sizes and ranges read back across chunks/packs, a damaged chunk fails only its ranges, swapped packs and another key refused, whole-pack reading (hash per pack, missing pack), a chunk that fails once (an I/O error or a flipped bit) read again past the cache and counted intact (also in the restore reader), one that never reads right damaged after the retries (none with 0), a whole-pack hash retaken, a pack that cannot be written stops the command, the stored form (plain and zstd frames) round trips and `HashAndMeasure` = written size, never more than planned, `Room` |
| `SecureDeleteTests` | every byte overwritten (also a file small enough to live in the MFT), a wiped folder gone with its read-only files, folders not to wipe |
| `DiscReadTests` | uncached reads give the file's bytes from anywhere (sizes around block and sector edges), the handle really bypasses the cache (a read that is not whole sectors is refused) |
| `ReedSolomonTests` | GF(256), repair of up to m blocks (data + parity), unrecoverable, truncated / missing files |
| `ProgramOnDiscTests` | every disc gets `_program` (the stand-ins), verify checks it (a broken source zip, a missing exe), `--without-program`; the plan keeps room for it, restage adds it only where there is room; the exe's resources: the license files as in the repo, the source zip of the tree (no build output); the repo's URL in both READMEs and `version` |
| `LegacyNamesTests` | the bdxl 0.7.1 fixture (old names): the catalog opens as it is and takes the new names on its first save (`discloak.key`, `DCLKENC2` for every part, also the unread ones), the disc verifies (`--deep`, `--no-cache`) and restores byte for byte, a catalog rebuilt from the disc (`init --key-from` + `scandisc`) and from its old `_catalog`, a session appended by Discloak has the new names and the mixed disc verifies and restores as one |
| `InterruptTests` | an interrupted scan saves, marks nothing missing, and a rerun finishes; interrupted stage/restage/restore/verify/scandisc leave nothing half-done; a restage stopped after pack 2 continues from `.partial`, taking over only the packs whose files were checked (own non-parallel collection: `Interrupt` is process-wide; `EndToEndTests`, `CatalogTests` and `AgentTests` share it, as they read `Console.Out` or set `DISCLOAK_AGENT`) |
| `MasterKeyTests` | password/recovery envelopes, password change keeps the master key, envelopes bound to their catalog, `discloak.key` round trip, found under its old name `bdxl.key` and written under the new one (both: the new one wins; `MigrateName`), KDF limits, `BDXLENC2` round trips/another key/another purpose/tampering, recovery key checksum; key rings: rotation, an old envelope opens only the old keys, a `bdxl-key-1` file opens and is rewritten as a ring |
| `AgentTests` | in-process server on its own pipe with a test clock: hand over / get / lock, idle time restarts with use, maximum, Win+L and sleep per catalog, other protocol, second agent refused, commands use the agent instead of the password |
| `CompressionTests` | auto/on/off, a location that fits only compressed: scan counts, plan, stage at the counted size, verify, restore, restage at the recorded level, level and codec from the manifest (catalog and manifest record `codec`); a copy without a codec is zstd, an unknown codec is refused with its reason (restore, verify); recount on a new level, alias rename, clear |
| `KeyRotationTests` | after a rotation: catalog under the new key, history still checks, old disc verified/restored via the ring, new disc on the new key, each disc's discloak.key opens only its keys (old password: old key only), leak simulation with `init --key-from`, append + restage keep the disc's key, a broken catalog is not rotated |
| `CatalogTests` | nothing readable in an encrypted catalog, file list split per location (unchanged parts not rewritten, stale parts removed, rollback), unsplit list read and split + too-old marker, compression, damage, HMAC against mixed saves + accept, snapshots (never `discloak.key`), pruning, rollback, `init` + wrong password, migration (history too, `--force`, interrupted), export/import, import wipes the export only once saved (`--keep`, refused folders, a failed import, a file held open: exit 2), password change + recovery key, `password check`, backup restore into empty/own/other catalog |
| `OptionsTests`, `SizeTextTests` | argument parsing, flags, dates in UTC, sizes and money |
| `EndToEndTests` | real commands via `Program.Main`: stage/verify/burn/restore, `cost report` and `discs` line up labels of any length, `version` (and `--version`) and the build every staged disc records (disc.json, the catalog, `discs`, kept by restage), discard gives numbers back down to the highest disc still in the catalog (burn the 4th: the next is the 5th; burn the 1st: the 2nd), what verify prints (what it checks, every file with `--deep` lined up as `OK`/`DAMAGED`, a damaged pack as soon as it is read), `verify --packs` (only those packs; with `--deep` whole files checked, partial ones decrypted; the advice to burn a damaged pack again; parsing), `verify --mark-burned` (only when everything checks out; refused with `--packs`; the advice for a burned disc), damaged copies recorded by verify (plain: every file in a damaged pack; `--deep`: only the failing ones) stop counting (pending again, status, find), stay marked on a repeat, lose the mark with a WARNING when read back intact (`--deep`, `--packs`), and restage clears them, verify checks the disc's other files (each kind of damage found by a plain verify, with and without `--no-cache`, `_catalog` against its repair data), manifest hashes recorded by the first `--deep` verify of an older disc and the manifests compared with the catalog, progress lines of stage/restore/restage, of equally full discs the one with the largest file staged first, tiny files in one pack, a damaged pack (verify names it, restore loses only its file), restore from a copy on another disc, restage taking intact packs from the old disc and rebuilding the damaged one, restage without a source (refused / `--allow-partial`), 1 vs 4 scan threads give the same catalog, moved files keep their record (also across separately scanned locations), split restore one drive at a time, lost + restage (same layout), hand-edited catalog |
| `EncryptedDiscTests` | nothing on a disc shows names/hashes/counts, two discs with different files look alike, `discloak.key` + README + packs.sha256 + key ids on the disc, verify + restore, a lost catalog rebuilt with `init --key-from` + `scandisc` (wrong/no password refused; unverified until `verify`), plain discs refused, old and new discs across a password change, `_catalog` repaired without and restored with the password, standalone backup, restage keeps the layout, a disc of another key refused (restage, restore) |
| `AppendTests` | only new/changed files in the session's packs (numbering continued), the session's manifest hash and build recorded (and dropped by discard), session manifest of its own (merged on the disc in session order), burn/verify/restore/scandisc of the merged disc, free space with reserve per session, discard of the session only (packs too), refusals, the disc's own key and nothing telling in the session, a disc of another key refused (catalog or `--mount`) |
| `CopiesTests` | per-location copies on different discs, never two on one disc, staged copies count for planning but only burned ones as made, lost discs, higher number of two locations, not onto an `--append` disc that holds it, split parts twice, end to end (stage, burn, restore from either disc, lost + restaged, alias rename) |
| `MirrorTests` | an empty folder gets a copy that every save keeps in step (and opens as a catalog), without the mirror only read-only commands run (password change refused too), a missed save fixed by `mirror sync`, a damaged catalog taken back with `mirror restore` (also a listed-but-damaged file), a newer mirror, password change reaches the mirror, folder rules, `mirror none` while disconnected |

## Suggested next steps
Ideas not built yet:
   - `burn` command that runs an external burner from a configurable command template, then `verify` + `burned`.
   - Faster disc reads (`restore`, `verify`). Do NOT read several files from the same
     optical disc in parallel: a CD/DVD/BD drive has one read head, so concurrent reads make it seek back and forth
     (slow seeks, spin-speed changes) and total throughput drops well below one sequential reader. The disc is the
     bottleneck (BDXL reads at roughly 18-36 MB/s; SHA-256 and AES-GCM are far faster), so instead:
     - one sequential reader per physical drive (parallel across different drives is fine);
     - pipeline per drive: read the next file / chunk while the previous one is hashed, decrypted and written
       (read → bounded queue → hash/decrypt/write), with large read buffers;
     - read in on-disc order (by `At`: restore already sorts its jobs so; the packs lie in number order).
     `scan --threads` stays as it is: it reads from HDD/SSD sources, where parallel hashing does help.
