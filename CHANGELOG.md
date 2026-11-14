# Changelog

What changed in each version of Discloak, called **bdxl** before 0.8.0 (the entries for older versions keep the old
name). Versions are `major.minor.patch`; see [CONTRIBUTING.md](CONTRIBUTING.md#versions) for what raises which part.
`discloak version` shows the version of an exe.

## 0.11.0 - 2026-11-14

- `stage refresh <folder>` brings staged disc folders (not burned yet) up to date, for running just before burning:
  the catalog backup (`_catalog\`) is written again from the catalog as it is now, `_program\` is replaced by the
  program running now (if it is another one), `README.txt` and `discloak.key` are written again if they differ. A disc
  staged a while ago, or together with others (`--max-discs`), then carries the latest catalog and program. Give one
  disc folder or the staging folder. A folder gets what it already has; `--add-catalog` and `--add-program` add the
  missing ones. Packs, manifest, `packs.sha256` and `disc.json` stay as they are. Folders of burned, discarded or
  restaged discs are skipped; a folder whose new files would not fit stays as it was. `--dry-run` shows what would change.

## 0.10.0 - 2026-11-14

- **verify records the copies it finds damaged** in the catalog (new optional fields `damagedUtc` and `damage` on a
  copy; older versions ignore them). Without `--deep`: every file with data in a damaged pack; with it: only the files
  that do not come back intact. A damaged copy no longer counts as a backup: a file still on its source is pending
  again, so the next `stage` makes a new copy; `status` warns, `find` shows the mark, `restore` tries such a copy last.
  A later verify that reads a marked copy back intact removes the mark with a warning (failing, then passing, points
  to unreliable reads or a wearing disc); `restage` of the disc removes the marks too.
- **verify reads again before it calls a pack damaged.** A read that fails, or a pack or chunk that comes back wrong,
  is tried again twice, from the disc itself (past the Windows file cache): a drive or its cable can fail a read once
  and get it right the next time. Such a read is shown as `RE-READ` - the data is intact, but it is a warning sign about
  the disc, the drive or its connection - and counted in the summary. `--retries N` sets how often (0: never).
  `restore` retries the same way.
- `verify --packs 312,680-690` checks only those packs (with `--deep`: decrypts them, checks every file wholly in them
  against its hash, and the part of a file that reaches beyond them) - for a pack that failed once, or one burned again.
- `verify --mark-burned` marks the disc (or its staged session) as burned when everything checked out: every pack,
  every file with `--deep`, and the disc's other files. Otherwise it says why not and leaves it staged.
- After a damaged pack, verify says what to do: on a disc not marked as burned yet, burn only those packs again from
  the staging folder as a new session (if the disc is not finalized; the pack paths and the check to run are printed);
  on a burned disc, how to restore or rebuild it.
- `verify --deep` prints a checked file as `OK       name (size)` or `DAMAGED  name (size): why`, lined up with the
  disc's other files, without the progress in front of it (the status line still shows the progress).
- Docs: the backing-up workflow leaves a new disc open until it has passed `verify`, and keeps the staging folder until
  then; what a damaged pack means for the copies and the ways out; room in the reserve for burning a pack again.
  Getting Started says the same. The file system to burn with: any the disc supports (only BD-R XL needs UDF 2.50 or
  later), no longer UDF 2.50 everywhere.
- Code: `System.Threading.Lock` for the locks that were plain objects.

## 0.9.1 - 2026-11-04

The repository, ready to be public; the program itself is unchanged.

- `README.md` (what Discloak is, getting it, the first commands), `SECURITY.md` (how to report a vulnerability
  privately), `.gitattributes` (LF line endings everywhere but Windows scripts).
- CONTRIBUTING: a section on pull requests, and how a version is released.
- `tools\release.ps1 <tag>` builds a tagged version's release files from a fresh worktree of the tag: the exe with
  its licenses, the source and the release notes (the version's changelog section; no checksum file: GitHub shows
  each file's SHA-256).
- `tools\publish-wiki.ps1` publishes `docs\` to the [wiki](https://github.com/oustish/discloak/wiki): links between
  pages become wiki links, links into the repository full addresses, and a footer names the version and commit.

## 0.9.0 - 2026-11-04

- **Every disc carries the program that staged it**, in `_program\`: `discloak.exe` (self-contained: it needs nothing
  installed, the .NET runtime is inside), its source code (`discloak-0.9.0-source.zip`, the exact code it was built
  from) and the licenses. The plan keeps room for it on every disc (about 40 MB); `--without-program` (stage, plan,
  restage) leaves it out. `verify` checks it (the exe's version is shown, the source zip must open), `restage` adds it
  where the disc's fixed layout still has room.
- The published `discloak.exe` is self-contained (about 37 MB): no .NET runtime to install. Its start takes about
  half a second (it unpacks itself). `THIRD-PARTY-NOTICES-dotnet.txt` holds the notices of the runtime inside it.
- One save brings a catalog from before 0.8.0 entirely to the new names: every part of it still in the old format
  is written again, also the parts of the file list the command did not read.
- `discard` gives a disc's number back: the next disc takes the number after the highest one still in the catalog
  (stage 4 discs, burn the 1st, discard the others: the next is the 2nd again; burn the 4th instead, and the next is
  the 5th - free numbers below a disc that stays are not reused). Lost and destroyed discs keep their numbers.
- Fixed: a catalog from before the label counter existed continues after its highest label number again; 0.8.0
  looked only at labels starting with `DISC-`, not the old default `BDXL-`. Now every label counts, whatever its prefix.
- The README on every disc, the catalog backup's README and `discloak version` name the project's home:
  https://github.com/oustish/discloak.

## 0.8.0 - 2026-11-04

**bdxl is now Discloak.** The command and the exe are `discloak` (`discloak.exe`); everything else follows:

- Environment variables: `DISCLOAK_PASSWORD`, `DISCLOAK_NEW_PASSWORD`, `DISCLOAK_RECOVERY_KEY`, `DISCLOAK_AGENT`,
  `DISCLOAK_AGENT_PIPE`. The old `BDXL_*` names are not read any more.
- The agent lives in `%LOCALAPPDATA%\discloak`; an agent started by bdxl is not found by Discloak (`bdxl lock`, then
  `discloak unlock`). `%LOCALAPPDATA%\bdxl` can be deleted.
- New discs are labelled `DISC-0001`, ... by default (a catalog that set its own prefix keeps it).
- New files get new format names: `discloak.key` (key file, format `discloak-key-2`), `discloak-pack-1` (`disc.json`),
  `DCLKPAK1` (packs) and `DCLKENC2` (catalog files, manifests). **Everything bdxl wrote is read as it is**, under its
  old names (`bdxl.key`, `bdxl-key-2`, `bdxl-pack-1`, `BDXLPAK1`, `BDXLENC2`) - burned discs forever. A catalog's
  `bdxl.key` becomes `discloak.key` on its next save; a disc with a session appended now holds both key files.
- Once Discloak has saved a catalog, or staged a disc, bdxl (0.7.1 and before) cannot read it any more.
- Before 1.0.0 a breaking change like this one raises the minor version (CONTRIBUTING.md).

## 0.7.1 - 2026-11-03

- License: [PolyForm Shield 1.0.0](LICENSE.md) - free to use for any purpose, not to build a competing product on;
  [LICENSING.md](LICENSING.md) explains it and sets the rules for the name and the format names.
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) holds the licenses of the components in `bdxl.exe`. `dotnet publish`
  puts all three next to the exe.
- `bdxl version` shows the copyright and the license.

## 0.7.0 - 2026-11-03

- `catalog import` wipes the plain export once the import is saved: every file in the folder overwritten with random
  bytes and deleted, and the folder removed. `--keep` leaves it. A folder with subfolders or links, or one that looks
  like a catalog, is refused before anything is imported.
- Fixed: `cost report` lines up its columns for labels longer than 12 characters (and amounts of any size); `discs`
  lines up labels of different lengths.

## 0.6.0 - 2026-11-03

The first numbered version. What bdxl does at this point:

- **Catalog**: every file with its SHA-256, the discs and the blank discs, encrypted with a master key that the password
  (Argon2id) and an optional recovery key unlock; integrity check with HMAC, version history and rollback, mirror,
  backups with Reed-Solomon repair data, key rotation, the `unlock` agent.
- **Planning and staging**: discs filled exactly (sectors, file system entries, the manifest), the fullest disc first,
  files split across discs (`--split`), several copies per location (`copies`), duplicates stored once, compression per
  location (zstd), sessions appended to a burned disc (`--append`).
- **Discs** (format `bdxl-pack-1`): every file laid end to end in one stream, cut into 64 MiB packs, each encrypted on
  its own (AES-256-GCM); the manifest encrypted and padded. Nothing on a disc shows file names, sizes or count.
- **Checking and recovery**: `verify` (pack hashes, `--deep` decrypts every file, `--no-cache` reads past the Windows
  file cache, and the disc's other files: disc.json, packs.sha256, manifests, bdxl.key, `_catalog`), `restore` (from
  any mounted copy, a damaged spot costs only its files), `restage` (rebuilds a disc, reusing its intact packs),
  `scandisc` (a catalog rebuilt from the discs).
- **Progress**: `scan`, `verify`, `stage`, `restore` and `restage` show progress, speed and time left.

New in this version:

- `bdxl version` (and `bdxl --version`): the version, Debug or Release, the commit and branch it was built from (`*`:
  with uncommitted changes) and the build time.
- Every staged session records the build that made it: `"createdBy"` in `disc.json`, and in the catalog per session
  (`discs` shows it).
- Compressed copies record their codec (`"codec": "zstd"`) in the catalog and in the manifest. A copy without one (staged
  before 0.6.0) is zstd; a codec this version does not know is refused with the reason, not reported as damage.
