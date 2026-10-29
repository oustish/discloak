# bdxl – handoff notes (state as of 2026-10-29)

## Status
- C# / .NET 10 console app (`net10.0`), 28 source files under `src/`, single namespace `Bdxl`, classic `Main` (no top-level statements).
- Builds cleanly (0 warnings). Smoke-tested by hand on 2026-10-29 in a temp folder: every command was run, including
  encryption, tamper detection, lost/restage, catalog rollback, backup repair and scandisc. There are still no automated tests.
- Fixed during that smoke test: UTF-8 console output, `--date` stored one day early (local midnight to UTC), false
  "DATA LOST" for moved files (lost + status), `verify` writing a history snapshot even when nothing changed,
  `find`/`restore` not matching `alias/path` or on-disc names, restore listing ignored files as lost, and `stage`
  now warns (exit 2) when the staged folder (with manifest/_catalog) is larger than the disc.
- Later the same day: `--split` added (see below) and the last small issues fixed (`plan` disc estimate ignores
  files larger than any disc, `ignore remove` of an unknown rule exits 1, `stage` exits 2 when nothing could be staged).

## Build / run
```
dotnet build -c Release
dotnet run -- help                 # all commands
dotnet run -- <command> --help     # details
dotnet run -- workflow             # sample session
```
Every command takes `--catalog <dir>` (default `./catalog`).

## Source layout
| Folder | Contents |
|---|---|
| `src/Program.cs` | `Main`: parse args → find command → load + integrity-check catalog → run. Exit codes: 0 ok, 1 error, 2 partial/not everything fits, 3 problems found, 4 catalog broken |
| `src/Cli/Options.cs` | Arg parser. `Flags` set = options without a value (add new boolean options there!) |
| `src/Cli/CommandTable.cs` | `CommandSpec` list: name, group, summary, usage, help text, handler, `allowBrokenCatalog`, `needsCatalog`. Also `HelpCommand` |
| `src/Cli/Ui.cs` | `Confirm` (`--yes`), password prompt (`BDXL_PASSWORD` or `--password-env VAR`, else hidden typing) |
| `src/Model/Models.cs` | `FileRecord`, `BackupRef`, `DiscRecord`, `BlankDisc`, `ManifestLine`, `DiscInfo`, `RepairMeta`, `Bin`, `PlanResult`, `DiscFile`, `UserException` |
| `src/Model/Settings.cs` | `settings.json`: label prefix + never-reset counter, currency, history-keep, default-threads, ignore rules |
| `src/Storage/Catalog.cs` | Load/Save, integrity (`catalog.hash.json`), snapshots in `history/`, rollback |
| `src/Storage/JsonStore.cs`, `Hashing.cs` | JSONL/JSON read+write (atomic via .tmp), SHA-256 helpers |
| `src/Core/` | `Packer` (best-fit-decreasing onto fixed blank discs), `GitIgnore`, `FileCrypto` (+`PasswordPolicy`, `KeyCache`), `ReedSolomon` (`Gf`, `Rs`), `SizeText` |
| `src/Services/` | `Ctx` (what every command gets), `Archive` (dedup, planning, labels, ignore application) |
| `src/Commands/` | One file per command group (see table below) |

## Commands → files
| Command | File |
|---|---|
| scan | ScanCommand.cs |
| plan, stage | PlanStageCommands.cs (also `DiscFiles.Write`: manifest.jsonl + disc.json) |
| burned, verify, discard, discs, lost, destroyed, found | DiscCommands.cs |
| restage, restore | RestageRestoreCommands.cs |
| scandisc, discjson | DiscImportCommands.cs |
| find, status | InfoCommands.cs |
| blanks | BlankCommands.cs |
| ignore | IgnoreCommand.cs |
| cost | CostCommand.cs |
| purge, alias, settings | MaintenanceCommands.cs |
| catalog (check/accept/history/rollback/backup create\|verify\|restore) | CatalogCommand.cs |
| workflow | WorkflowCommand.cs |

## Key rules / invariants
- **Catalog files:** `files.jsonl`, `discs.jsonl`, `blanks.jsonl`, `settings.json` (`Catalog.CoveredFiles`).
  `catalog.hash.json` holds their SHA-256 hashes. Every `Catalog.Save()` rewrites all four files plus the hash file.
- **Integrity states:** `Fresh` (no catalog yet), `Legacy` (no hash file yet: allowed, and fixed by the next save),
  `Ok`, `Broken` (only commands with `allowBrokenCatalog` run; `Save()` throws).
- **Versioning:** the first `Save()` in a process copies the on-disk catalog plus its hash file to
  `history/<yyyyMMdd-HHmmss>-<command>/`. That is one copy per command, pruned to `HistoryKeep`.
- **Dedup:** content identity is `FileRecord.Sha256`, the hash of the plaintext.
  - Pending = still on the source, not ignored, already hashed, and no live disc holds that hash (`Archive.MakePlan`).
  - Duplicates get a `BackupRef` with `Alias = true` that points at the stored copy (`Archive.LinkDuplicates`).
  - "Live" disc = state is not `lost` or `destroyed`.
- **Ignored files:** `Sha256 = ""` (never hashed), `Ignored = true`. Any code that uses the hash must skip empty hashes.
  `restage` ignores the `Ignored` flag on purpose.
- **Encryption:** stored path = `<alias>/<rel>.enc`.
  - `BackupRef.Sha256` = hash of the plaintext; `StoredSha256` = hash of the ciphertext (used by `verify` without a password).
  - Format `BDXLENC1`: header (iterations, salt, nonce prefix), then 1 MiB AES-GCM chunks; the AAD is the chunk number plus a last-chunk flag.
  - One salt per staged disc; keys are cached per salt.
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
- **Labels:** `Settings.LabelPrefix` + `LabelCounter` (`D4`). The counter only ever goes up (`Archive.NextLabel`).
  For old catalogs it is initialised from the existing `BDXL-nnnn` labels.
- **Blank discs:** `stage` uses up a `BlankDisc` and copies its capacity, reserve and cost to the `DiscRecord`.
  `discard` gives the blank disc back.
- **Ignore rules:** `.gitignore` syntax, matched against `RelPath` (relative to the scanned folder).
  - `scan` evaluates each file it sees.
  - `ignore add/remove/apply` re-evaluates the whole catalog (`Archive.ApplyIgnoreRules`).
- **Scan safety:**
  - A folder that can't be read: files under it are not marked missing.
  - An unreachable location during a rescan of everything: skipped completely.

## Suggested next steps
1. Add a small test project (xUnit) with these tests:
   - `GitIgnore`
   - `Packer`
   - `Rs` round trip
   - `FileCrypto` round trip (including wrong password and truncated file)
   - `Catalog` integrity and snapshots
   - `Options` parsing
2. Smoke test on a temporary folder: `blanks add` → `scan` → `plan` → `stage` (with and without `--encrypt`)
   → `verify` against the staged folder → `burned` → `restore` → `lost` → `restage`.
3. Ideas not built yet:
   - `burn` command that runs an external burner from a configurable command template, then `verify` + `burned`.
   - ISO/UDF image creation by calling oscdimg / mkudffs (UDF 2.50 or later is needed for files over 4 GiB).
   - Encrypting the `_catalog` backup and the manifest on encrypted discs (file names are currently readable).
   - Restore-time progress output, and multithreading for `verify` / `scandisc --method hash`.
