# Command Reference

[Home](Home.md) › Command Reference

`discloak help` lists the commands; `discloak <command> --help` shows the details of one.

**Options for every command**

| Option | |
|---|---|
| `--catalog <dir>` | catalog folder (default `.\catalog`) |
| `--password-env VAR` | take the password from this environment variable (default `DISCLOAK_PASSWORD`) |
| `--yes` | answer yes to confirmations (needed when input is redirected) |
| `--help`, `-h` | help for the command |

Sizes accept bytes or units: `25GB` (10^9), `90GiB` (2^30), `500MB`, `1.5TB`. Durations: `90s`, `30m`, `8h`, `1h30m`, `2d`.
Exit codes: see [Troubleshooting](Troubleshooting.md#exit-codes).

Contents: [Indexing and planning](#indexing-and-planning) · [Making discs](#making-discs) ·
[Existing discs](#existing-discs) · [Looking things up](#looking-things-up) · [Recovery](#recovery) ·
[The catalog itself](#the-catalog-itself)

---

## Indexing and planning

### init
Create an encrypted catalog, encrypt an old plaintext one, or start one from a disc's key.
```
discloak init [--recovery-key] [--kdf-memory <size>] [--kdf-passes N] [--force]
discloak init --key-from <disc or folder> [--recovery]
```
| Option | |
|---|---|
| `--recovery-key` | also create a recovery key (shown once) |
| `--kdf-memory`, `--kdf-passes` | Argon2id settings; by default measured so unlocking takes ~2 s (below 256 MiB: warning) |
| `--force` | encrypt an old plaintext catalog even if it fails its own integrity check |
| `--key-from <path>` | a new, empty catalog with the master key(s) from `discloak.key` in that folder (a disc, a catalog backup) |
| `--recovery` | with `--key-from`: unlock it with the recovery key instead of the password |

### scan
Index files: a folder, or every known location.
```
discloak scan [<folder>] [--name <alias>] [--threads N] [--full]
```
| Option | |
|---|---|
| `<folder>` | omitted: rescan all known locations (unreachable ones are skipped) |
| `--name <alias>` | the location's name in the catalog and on discs (default: folder name) |
| `--threads N` | hash with N threads (default: setting `default-threads`) |
| `--full` | hash every file again, even unchanged ones |

Unchanged files (same size + time) are not read again; missing files are marked missing; moved files are
recognised by hash. Ignored files are catalogued but not hashed. With compression on, also counts compressed sizes.
A file that changes while being read is skipped (left as it was) and read again by the next scan.
Ctrl+C stops cleanly (exit 130); nothing is marked missing in the interrupted location.

### compression
Compression per location.
```
discloak compression [list]
discloak compression set <alias> off|auto|on [--level N]
discloak compression set --default off|auto|on [--level N]
discloak compression clear <alias>
```
`auto` skips already-compressed formats; level 1–19 (default 3). After a change run `scan`. See [Compression](Guide-Compression.md).

### copies
How many copies, each on a different disc, a location's files should have.
```
discloak copies [list]
discloak copies set <alias> N
discloak copies set --default N
discloak copies clear <alias>
```
N = 1–9 (default 1). Planning counts staged and burned discs; `copies list`, `status` and `find` count burned ones.
`copies list` exits with 2 while some file has fewer burned copies than wanted. See [Several Copies](Guide-Copies.md).

### plan
Which pending files would go on which blank disc. Changes nothing.
```
discloak plan [--split] [--append <label> [--mount <path>]]
```
| Option | |
|---|---|
| `--split` | cut files larger than every blank disc into parts |
| `--append <label>` | what a new session of this burned disc would hold |
| `--mount <path>` | with `--append`: the disc is in the drive (label checked, drive's free space used) |

Exit code 2: not everything fits.

### blanks
Your stock of blank discs.
```
discloak blanks [list]
discloak blanks add <count> [--size S] [--reserve R] [--cost P]
discloak blanks reserve <R> [--size S]
discloak blanks cost <P> [--size S]
discloak blanks remove <id|all> [--size S]
```
Default size: 96,882,130,944 bytes (BD-R XL 100 GB). `--size` on `reserve`/`cost`/`remove`: only blanks of that size.

### ignore
Ignore rules (`.gitignore` syntax), relative to each scanned folder.
```
discloak ignore [list]
discloak ignore add <pattern>...
discloak ignore remove <pattern|number>...
discloak ignore test <path>
discloak ignore apply
```

---

## Making discs

### stage
Write the next disc folder(s), ready to burn.
```
discloak stage <destDir> [--max-discs N] [--min-fill 0.9] [--split] [--with-catalog] [--without-program] [--append <label> [--mount <path>]]
```
| Option | |
|---|---|
| `--max-discs N` | stage up to N discs (default 1): the fullest first; of equally full ones (to 0.1%), the one with the largest file first |
| `--min-fill 0.9` | only stage discs at least 90% full |
| `--split` | cut files larger than every blank disc into parts |
| `--with-catalog` | add a catalog backup with repair data (`_catalog\`) |
| `--without-program` | leave out `_program\` (below): its room on the disc goes to files |
| `--append <label>` | a new session for a burned, not finalized disc ([Appending](Guide-Appending.md)) |
| `--mount <path>` | with `--append`: the disc is in the drive |

Folder: `<destDir>\<label>\` (`<label>.s2` … for sessions) holding the packs `data\NNN\NNNNNN.pack` (64 MiB each; a
session's packs continue the disc's numbering), `manifest.enc` (a session: `manifest.sN.enc`, only its own files),
`packs.sha256`, `disc.json`, `discloak.key`, `README.txt`, `_program\`, optional `_catalog\`. See [Files and Formats](Files-and-Formats.md#a-staged--burned-disc).
`_program\` is the program that staged the disc: `discloak.exe` (self-contained, needs nothing installed), its source
code and the licenses, about 40 MB - so the disc can be read with nothing else. The plan keeps room for it on every
disc and every appended session.
Ctrl+C finishes the disc with what was copied. A file that changed since `scan` is left out; its space in the pack stays unused.
Every file is printed as soon as it is stored, with the disc's progress, speed and time left
(`  [3/302   12.4%    85.3 MB/s  ~3h 12m left] Video/a.mkv (4,212,334,123 bytes)`); while a large file is copied, a
status line shows how far it is.

#### stage refresh
Bring disc folders that are staged but not burned yet up to date, just before burning them.
```
discloak stage refresh <discFolder|stagingFolder> [--add-catalog] [--add-program] [--dry-run]
```
Give one disc folder (`E:\Staging\DISC-0002`) or the staging folder (every disc folder in it). In each folder of a
disc (or appended session) that is staged and not burned yet:

| | |
|---|---|
| `_catalog\` | written again: the catalog as it is now (a disc staged together with others, or a while ago, has an older one) |
| `_program\` | replaced by the program running now, if it is another one (another version or build) |
| `README.txt`, `discloak.key` | written again if they differ (`discloak.key`: the catalog's key file, e.g. after a password change) |

A folder gets what it already has: `--add-catalog` / `--add-program` add `_catalog\` / `_program\` where they are
missing; to take one out, delete it by hand. The packs, the manifest, `packs.sha256` and `disc.json` are not touched,
so `verify` works as before. Skipped, with the reason: folders of burned discs, of discs no longer in the catalog
(discarded), of other catalogs, and old folders whose `packs.sha256` does not match the catalog (from before a
discard or `restage`). If the new files would not fit on the disc (a catalog backup larger than the reserve), the
folder stays as it was (exit code 2). `--dry-run` only shows what would change.

### burned
Mark a staged disc, or its staged session, as burned.
```
discloak burned <label> [--date yyyy-MM-dd]
```
`verify … --mark-burned` does it after a verify that finds nothing wrong.

### verify
Re-read a disc and compare it with the catalog.
```
discloak verify <label> <mountedDiscPath> [--deep] [--packs 312,680-690] [--files <text>] [--no-cache] [--retries N] [--mark-burned] [--as-disc]
```
**A disc, or a folder.** What verify finds is recorded in the catalog (damage marks set and cleared, `unverified`
cleared, `--mark-burned`) only when it reads a **disc in an optical drive**. A folder anywhere else - the staging folder,
a copy of a disc on another drive - is checked the same way and its damage reported, but nothing is recorded: it says
nothing about the disc. The second line of the output says which it is (`a disc in an optical drive: what verify finds
is recorded in the catalog`, or `a folder on a fixed drive (E:\), not a disc in an optical drive: nothing is
recorded`). A mounted disc image (ISO) counts as a disc, as Windows shows it in an optical drive.
Compares every pack's SHA-256 with the catalog (no password needed for that). `--deep` also decrypts every pack and
checks every file against its hash, in the same single read. A damaged pack is named with the files that have data in
it. Exit code 3: damaged or missing packs, or files that do not come back intact.

The copies it finds damaged are **recorded in the catalog** (without `--deep`: every file with data in a damaged pack;
with it: only the files that do not come back intact). They no longer count as backups: files still on their source
are pending again, and `status` and `find` show the marks. A later verify that reads the packs that were wrong back as
the catalog has them (`--packs` of just those is enough), or `--deep` that reads the whole file back intact, removes
the mark, with a warning. Damage `--deep` finds in packs that read back exactly as staged was already in what was
staged: only `--deep` clears that ([What verify records](Guide-Backing-Up.md#damaged-copies)). See [Backing Up › If verify finds a damaged pack](Guide-Backing-Up.md#if-verify-finds-a-damaged-pack)
for what to do, before and after `burned`.

| Option | |
|---|---|
| `--deep` | also decrypt every pack and check every file against its hash |
| `--packs L` | only these packs: `312`, `312,683`, `300-310`. With `--deep`, every file whose data lies wholly in them is checked against its hash; a file that reaches beyond them only has its part in them decrypted (`OK … its data in pack(s) 312 (the file spans packs 300-380: not checked against its hash)`). The disc's other files are checked as always. For checking a pack again after burning it a second time, or a pack that failed once. |
| `--retries N` | how often a read that fails, or a pack or chunk that comes back wrong, is tried again (default 2; `0`: never). Every retry reads from the disc itself, past the Windows file cache. A read that only came right on a retry is printed as `RE-READ`: the data is intact, but it is a warning sign about the disc, the drive or its cable. |
| `--files T` | only the packs of the files matching `T` on this disc (matched as `find` and `restore` match: part of the path, a hash prefix, or `*`). With `--deep` each matching file is checked whole against its hash - the quickest full check of particular files. With `--packs`: the packs of both. [`find`](#find) shows each copy's packs. |
| `--mark-burned` | if every pack, every file (`--deep`) and the disc's other files check out, mark the disc (or its staged session) as burned, as [`burned`](#burned) does, with today's date. Otherwise it says why not and leaves the disc staged. Only for a disc in an optical drive; not with `--packs` or `--files`. |
| `--as-disc` | the folder is the disc, although its drive does not say it is an optical drive: what verify finds is recorded |
| `--no-cache` | read every byte from the disc itself. Without it, Windows may hand over what it still keeps in memory from an earlier read of this disc (since it was inserted; ejecting the disc empties that): the disc's own data, but not proof that it reads back now. Use it to re-check a disc that has stayed in the drive. It also keeps the 100 GB read from pushing other programs' data out of memory. |

Before the packs, the disc's other files are checked, a line each:

| File | Checked |
|---|---|
| `disc.json` | readable, this catalog's, the disc's key and number of sessions |
| `packs.sha256` | lists the same pack hashes as the catalog |
| `manifest.enc`, `manifest.sN.enc` | SHA-256 as the catalog has it; `--deep` also decrypts each one and compares the files it lists with the catalog |
| `discloak.key` | readable, this catalog's, with a password envelope |
| `README.txt` | readable |
| `_program\` | every file readable, the source zip opens, `discloak.exe` present (its version is shown) |
| `_catalog\` | every file against its repair data (as `catalog backup verify` does; a damaged spot that cannot be read counts as damaged and is repairable like any other) |

A disc staged before the catalog kept the manifests' hashes (before 2026-11-03) has none in the catalog: a plain verify then
only reads its manifests; its first `--deep` verify that finds them intact records them.

It starts by saying which disc it reads, from where, how many packs, bytes and files, and what it checks. While it
reads, a status line shows the pack, the progress by bytes, the speed, the time left and the file whose data is
being read (rewritten in place; not written when the output goes to a file). With `--deep` every file is printed as
soon as it is checked (`OK` or `DAMAGED`, then why), lined up with the disc's other files. A damaged pack is printed as
soon as it is read, and listed again at the end. Example (`--deep`; the last line is the status line):
```
Verifying DISC-0001 in G:\: 1,485 pack(s), 99,658,266,840 bytes, 302 file(s)
  --deep: every pack is hashed and decrypted, and every file is checked against its hash
The disc's files:
  OK       disc.json
  OK       packs.sha256 (1,485 packs, as in the catalog)
  OK       manifest.enc (decrypts: 302 file(s))
  OK       the manifests list the 302 file(s) the catalog has on this disc
  OK       discloak.key (this catalog's, with a password envelope)
  OK       README.txt
  OK       _catalog (7 file(s) intact, checked against their repair data)

  OK       Video/2024-08 Holiday.mkv (6,182,453,120 bytes)
  RE-READ  pack 291, chunk 17 read correctly on attempt 2 (attempt 1: pack 291, chunk 17 cannot be read (...))
  OK       Video/2019 Wedding.mkv (14,326,902,784 bytes)
  [pack   312/1,485   21.0%    35.2 MB/s  ~37m 02s left] Video/2021 Trip.mkv
```

### discard
Forget a staged disc (or a staged session) that was never burned. Its files are pending again; its blank disc goes
back to stock. The folder is not deleted.
```
discloak discard <label>
```

---

## Existing discs

### scandisc
Record a disc made by discloak in the catalog, from its own manifests: how a lost catalog is rebuilt from the discs, or a
catalog brought up to date after a rollback. Discs written with other tools cannot be recorded.
```
discloak scandisc <mountPath> [--date D] [--capacity S] [--cost P] [--update] [--dry-run]
```
| Option | |
|---|---|
| `--date` | when it was burned (default: the date in `disc.json`) |
| `--capacity`, `--cost` | facts about the disc (`--capacity` lets `stage --append` add to it) |
| `--update` | merge into a disc already in the catalog |
| `--dry-run` | show, change nothing |

The copies count as *unverified* until `verify` has checked the disc's packs.

---

## Looking things up

### status
Sources, pending data, blank discs, discs; warns about files that exist nowhere any more.
```
discloak status
```

### find
Where a file is backed up: part of a path, or the start of a SHA-256.
```
discloak find <text|hashPrefix|*>
```
Each copy is shown with its disc, the disc's state, the burn date and the packs that hold it
(`DISC-0009 (burned, 2026-11-10, packs 300-380) current version`): for `verify --packs`, or re-burning a pack. A copy
`verify` found damaged is marked `[DAMAGED, …]`.

### discs
Every disc: state, files, packs, sessions, cost, the discloak build that staged it (its latest session), notes, free space.
```
discloak discs
```

### cost
```
discloak cost [report]
discloak cost set <label> <amount>
```
Cost per disc, per GB and per TB of data and of capacity, and the value of blank stock (GB = 10^9 bytes).

---

## Recovery

### lost / destroyed / found
```
discloak lost <label> [--reason text]          # may turn up again
discloak destroyed <label> [--reason text]     # final
discloak found <label>                          # a lost disc turned up
```
Copies on a lost/destroyed disc stop counting; files still on their source are pending again; files that exist
nowhere else are listed as DATA LOST (exit code 2).

### restage
Rebuild a disc's folder (same label, same files) to burn it again.
```
discloak restage <label> <destDir> [--from dirA;dirB] [--allow-partial]
```
| Option | |
|---|---|
| `--from` | folders holding the disc's packs: the damaged disc itself, a copy of it, or a `.partial` folder |
| `--allow-partial` | stage even if some files cannot be rebuilt (they are dropped from the disc's record) |

Same layout as before: a pack whose hash matches is copied as it is; any other is rebuilt from the source files and
encrypted again. The disc becomes *staged* until `burned`. Ctrl+C keeps `<label>.partial` for `--from`.
Every pack is printed when it is written, with the progress, speed and time left
(`[DISC-0001] pack 2 of 1,485: rebuilt       0.1%    35.2 MB/s  ~46m 50s left`).

### restore
Copy files back from mounted discs.
```
discloak restore <destDir> <text|hashPrefix|*> --mount F:\;G:\ [--disc label] [--source alias] [--flat] [--overwrite]
```
| Option | |
|---|---|
| `--mount` | drive(s)/folder(s) holding discs, separated by `;` |
| `--disc label` | the single mount has no `disc.json`: which disc it is |
| `--source alias` | only files of this location |
| `--flat` | no alias/path folders |
| `--overwrite` | replace existing files |

Only the parts of the packs a file needs are read. A file in a damaged spot is taken from another mounted disc with a
copy, if there is one; the rest is restored anyway. Exit code 2: more discs needed (listed); 3: failures.
It says how many files and bytes it reads from which mounted discs; every file is printed when it is done, with the
progress, speed and time left (`  [3/302   12.4%    35.1 MB/s  ~41m left] restored Video/a.mkv`), and while a large
file is read a status line shows how far it is. Files that already exist (EXISTS) do not count: progress and time
left are for what is still to be read.

---

## The catalog itself

### catalog
```
discloak catalog check
discloak catalog accept
discloak catalog history
discloak catalog rollback <id> [--force]
discloak catalog export <dir>
discloak catalog import <dir> [--keep]
discloak catalog backup create <dir> [--parity-percent 25]
discloak catalog backup verify <dir> [--repair]
discloak catalog backup restore <dir> [--force] [--recovery]
discloak catalog mirror status | sync | restore [--recovery]
```
| Sub-command | |
|---|---|
| `check` | verify the catalog only (exit 4 if broken) |
| `accept` | every file still decrypts but the hash list does not match: record it anew |
| `history` | older versions, newest first, each checked |
| `rollback <id>` | go back to a version (current one saved first); `--force` for a damaged version |
| `export <dir>` | write plain JSON into an empty folder (**not encrypted**) |
| `import <dir>` | replace the catalog with plain JSON from such a folder, then wipe the folder (every file in it overwritten with random bytes and deleted, the folder removed) once the import is saved; `--keep` leaves it. A folder with subfolders or links, or one that looks like a catalog, is refused before anything is imported. Exit 2: imported, but a file could not be wiped (open in another program) |
| `backup create <dir>` | encrypted catalog + `discloak.key` + Reed-Solomon repair data into `<dir>\catalog-<date>` |
| `backup verify <dir>` | check (and `--repair`) a backup, no password needed |
| `backup restore <dir>` | into an empty folder: with the backup's key (password or `--recovery`); into the same catalog: `--force`, current password stays; another catalog: refused |
| `mirror status` | is the mirror there and in step? (exit 2 if not) |
| `mirror sync` | copy the catalog over the mirror |
| `mirror restore` | take the catalog from the mirror (current one to history; the mirror's `discloak.key` and password come along) |

### password
```
discloak password change [--rotate-key] [--recovery] [--new-password-env VAR] [--kdf-memory <size>] [--kdf-passes N]
discloak password check [<folder>] [--recovery]
```
| Option | |
|---|---|
| `--rotate-key` | also start a new master key (older keys kept in the key ring; recovery key replaced) |
| `--recovery` | `change`: unlock with the recovery key (`DISCLOAK_RECOVERY_KEY` or typed) instead of the password; `check`: check the recovery key |
| `--new-password-env VAR` | the new password from this variable (default `DISCLOAK_NEW_PASSWORD`); else typed twice, the second time copied from where you saved it, then confirmed |
| `check [<folder>]` | does this password open the `discloak.key` of the catalog or of `<folder>` (a disc, a backup)? Shows when that password was set. Exit 0 = yes, 1 = no. Changes nothing, never uses the agent |

### recovery-key
```
discloak recovery-key create      # make one (replaces an existing one)
discloak recovery-key remove
```

### unlock / lock
```
discloak unlock [--idle 30m] [--max 8h]
discloak unlock --status
discloak lock [--all]
```
`DISCLOAK_AGENT=off` keeps a command away from the agent. See [Security](Guide-Security.md#unlocking-for-a-while).

### purge
Remove entries of files that are gone from their source and were never on a disc.
```
discloak purge [--dry-run] [--yes]
```

### alias
```
discloak alias [list]
discloak alias rename <old> <new>
```

### settings
```
discloak settings [show]
discloak settings set <key> <value>
```
| Key | Default | |
|---|---|---|
| `label-prefix` | `DISC-` | new discs: `<prefix><counter>`; one counter for all prefixes, which goes back only when `discard` takes the newest discs out (the next disc takes the number after the highest one still in the catalog) |
| `currency` | | shown next to costs |
| `history-keep` | 10 | catalog versions kept in `history\` |
| `default-threads` | 1 | default for `scan --threads` |
| `unlock-idle` | 30m | an unlocked catalog locks after this long without use |
| `unlock-max` | 8h | … and after this long at the latest |
| `lock-on-win-l` | yes | lock when Windows is locked |
| `lock-on-sleep` | yes | lock when the computer goes to sleep |
| `mirror` | (none) | a folder for the [catalog mirror](Guide-Mirror.md), or `none`; kept in `mirror.json`, not in the encrypted settings |

### workflow / help / version
```
discloak workflow          # a sample session (prints commands, runs nothing)
discloak help [<command>]
discloak version           # or: discloak --version
```
`version` shows which build this is:
```
discloak 0.8.0 (Release)
commit 3f2a1c9 on master, built 2026-11-04 12:00 UTC
full version: 0.8.0+3f2a1c9.20261104T1200Z
```
A `*` after the commit (and `-dirty` in the full version) means it was built with uncommitted changes. The full
version is what every staged disc records in its `disc.json` (`createdBy`). The changes of each version are in
`CHANGELOG.md` in the repository.

## Environment variables

| Variable | |
|---|---|
| `DISCLOAK_PASSWORD` | the catalog password (or name another variable with `--password-env`); for `init`, the new password |
| `DISCLOAK_NEW_PASSWORD` | the new password for `password change` (or name another variable with `--new-password-env`) |
| `DISCLOAK_RECOVERY_KEY` | the recovery key for `--recovery` |
| `DISCLOAK_AGENT=off` | do not use the agent |
