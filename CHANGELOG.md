# Changelog

What changed in each version of bdxl. Versions are `major.minor.patch`; see [CONTRIBUTING.md](CONTRIBUTING.md#versions)
for what raises which part. `bdxl version` shows the version of an exe.

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
