# Troubleshooting and FAQ

[Home](Home.md) › Troubleshooting

## Exit codes

| Code | Meaning |
|---|---|
| 0 | done |
| 1 | error (wrong arguments, wrong password, refused, nothing to do where something was expected) |
| 2 | partial: not everything fits (`plan`, `stage`), more discs needed (`restore`), data lost (`lost`), fewer copies than wanted (`copies list`) |
| 3 | problems found: damaged or missing packs, files that do not come back intact, a damaged or missing manifest, `packs.sha256`, `disc.json`, `bdxl.key` or `_catalog` file (`verify`, `restore`), files `restage` cannot rebuild, an unrecoverable backup file |
| 4 | the catalog failed its integrity check (only `catalog …` commands run), or its [mirror](Guide-Mirror.md) is not connected or not in step (only read-only commands run) |
| 130 | stopped with Ctrl+C (after cleaning up; see the command's message) |

## Common messages

| Message | What to do |
|---|---|
| `No catalog in … Create one with: bdxl init` | Wrong folder? Pass `--catalog <dir>`. Otherwise `bdxl init`. |
| `The catalog is encrypted: set the environment variable BDXL_PASSWORD, or run this in an interactive terminal.` | The command runs with redirected input (a script, a pipe). Set `BDXL_PASSWORD`, `bdxl unlock` first, or run it in a terminal. |
| `The password in BDXL_PASSWORD does not open the catalog …` | The variable holds an old or wrong password. |
| `Wrong password (3 attempts).` | `bdxl password check` tells when the password was set, to find the right entry; see [Forgotten password](Guide-Disaster-Recovery.md#forgotten-password). |
| `The two passwords differ; nothing was changed.` | During `password change` / `init`: the copy in your password manager is not what you typed first. Fix the entry, run again. |
| `Confirmation needed but input is redirected; add --yes.` | Add `--yes` in scripts. |
| `The catalog in … failed its integrity check, so no work is done` (exit 4) | [The catalog is damaged](Guide-Disaster-Recovery.md#the-catalog-is-damaged-or-fails-its-check). |
| `Cannot be read: files.jsonl line 1: 'b' is an invalid start of a value` (exit 4) | An **older bdxl** on a catalog a newer one has saved. Use the current version; nothing is damaged. |
| `Nothing that changes the catalog runs while the catalog mirror … is not reachable` | Connect the mirror drive; or `bdxl settings set mirror none`. See [Catalog Mirror](Guide-Mirror.md). |
| `… the catalog mirror … is not in step` / `fails its integrity check` | `bdxl catalog mirror sync` (or, if the catalog is the one that is wrong or behind, `catalog mirror restore`). |
| `The catalog was saved, but its mirror … is not reachable` | The drive went away during the command. Connect it, `bdxl catalog mirror sync`. |
| `No blank discs are registered.` | `bdxl blanks add …` |
| `Nothing staged: no blank disc would be full enough (see --min-fill).` | Lower or drop `--min-fill`, or wait for more data. |
| `Nothing staged: the pending files do not fit on any registered blank disc` | A file is larger than every disc: `--split`, or larger blanks. |
| `changed since last scan - not staged. Re-run 'scan'.` | The source changed after the scan. `bdxl scan`, then stage again. |
| `WARNING: … folder is … more than the disc holds` | Raise the reserve (`bdxl blanks reserve …`), `discard` the disc, stage again. |
| `Folder already exists: …` | A staging folder of that label is still there. Delete or move it. |
| `… is not burned yet. To put more on it: 'discard …' and stage again.` | `--append` works only on burned discs. |
| `Session N of … is staged but not burned yet.` | Burn it and run `burned`, or `discard` it. |
| `… encrypted with another key` / `This disc belongs to another catalog.` | The disc was made by another catalog (or another key). Use that catalog, or `init --key-from <disc>` into a new folder. |
| `Ignoring G:\: no disc.json there` | The disc has no `disc.json`: add `--disc <label>` (one mount at a time). |
| `… is not a bdxl disc (no disc.json)` (`scandisc`) | Only discs made by bdxl can be read into a catalog. |
| `DAMAGED  pack N …` (`verify`) | That pack does not match its hash (or does not decrypt). The files listed below it are the ones affected; restore them from another copy, and/or rebuild the disc with `restage … --from <the disc>` (the intact packs are reused). |
| `PROBLEM  manifest.enc: …` (`verify`) | The disc's manifest is damaged or differs from the catalog. The files are still restorable with this catalog; only `scandisc` (rebuilding a catalog from the disc) needs it. Rebuild the disc with `restage … --from <the disc>` to get a good one. |
| `PROBLEM  _catalog: …` (`verify`) | The catalog backup on the disc is damaged. "Repairable": `catalog backup restore` repairs it as it reads, or copy the folder and run `catalog backup verify <copy> --repair`. "Beyond repair": use the backup on another disc. |
| `PROBLEM  packs.sha256 / disc.json / bdxl.key / README.txt: …` (`verify`) | One of the disc's small files is damaged or missing. The catalog has what they hold; `restage … --from <the disc>` writes them again. Only `bdxl.key` matters without the catalog: keep a disc whose `bdxl.key` is intact (or the catalog backup). |
| `DAMAGED  … on BDXL-…: pack N, chunk C is damaged` (`restore`) | That file has data in a damaged spot. Mount another disc with a copy (`find` lists them); everything else is restored. |
| `A different folder is already catalogued as 'X'. Use --name <alias>.` | Each alias belongs to one folder; choose another name. |
| `This folder is already catalogued as 'X'.` | Rescan without `--name`, or rename with `bdxl alias rename`. |
| `The agent did not start (see …agent.log)` | Look at the log. Commands keep working with the password; `BDXL_AGENT=off` skips the agent. |
| `Windows did not let the agent leave this terminal's process group` | Closing that terminal ends the agent (and locks). Harmless. |

## FAQ

**Can a disc be read without bdxl?**
Yes. Its `README.txt` describes `bdxl.key`, the packs and the manifest with standard algorithms (Argon2id,
HKDF-SHA256, AES-256-GCM, zstd), enough to write a decryptor. You need the password valid when the disc was staged
(or the recovery key of that time). Checking it needs nothing at all: `packs.sha256` works with any `sha256sum`.

**What does someone who finds a disc learn?**
Its label and dates, how many sessions it has, and how much data it holds (to 64 MiB: the number of packs). Not the
names, sizes or number of the files: they are in equal, encrypted packs, and even the manifest is padded to whole
64 MiB. A `_catalog` backup on the disc (`--with-catalog`) is encrypted too, but its size grows with the number of
files in the whole catalog.

**What if I lose `bdxl.key`?**
It is in every catalog backup and on every encrypted disc. Restore a backup, or `init --key-from <disc>`.

**Is the password stored anywhere?**
No. Only `bdxl.key`, which holds the master key encrypted with a key derived from the password. With `unlock`, the
master key (not the password) is held in the agent's memory until it locks.

**Why does each command take a second or two before it starts?**
Deriving the key from the password is deliberately expensive (Argon2id, ~2 s), so guessing passwords is slow too.
`bdxl unlock` makes it a one-off.

**How fast is it with a large catalog?**
Every command reads the catalog (and checks it) before it starts. Measured with 1 million files (every one on a disc):
about 3 s and 1.5 GB of memory to load; `status` and `plan` about 4-5 s. It grows in proportion: 5 million files would
take roughly 15 s and 7 GB (estimated). Commands that never look at the file list (`blanks`, `settings`, `cost`, `discs`, `found`,
`recovery-key`) skip it and start at once. A [catalog mirror](Guide-Mirror.md) adds the time to read it back once.

**Why did a `scan` read files that had not changed?**
Their location's compression setting changed (or `--full` was given); their compressed size is counted again.

**How big should the reserve be?**
Enough for the file system's fixed overhead (a few tens of MB on a 100 GB disc; the packs, the manifest and the
padding of both are counted by the plan itself), the disc's folders and small files (a few hundred KB) and, with
`--with-catalog`, the catalog backup (~1.25× the catalog's `.enc` files; those take roughly 100-150 bytes per
catalogued file). `stage` warns when it does not fit.

**Which file system should I burn with?**
UDF 2.50 (the usual choice for BD-R). bdxl's file names are short and plain ASCII, so any burning program works.

**Why did `verify` run far faster than the drive can read?**
Windows keeps what it read from a disc in spare memory until the disc is ejected, so a second `verify` gets part of
the disc from memory. That is still the disc's data (read from it earlier), but not proof that it reads back now.
Eject and insert the disc again, or run `verify … --no-cache`, which reads every byte from the disc itself.

**Can I use DVDs, other discs, or folders on hard disks?**
Yes. Register blanks with their size; `verify`, `restore` and `scandisc` work on any folder laid out like a disc.
Discs written without bdxl cannot be brought into a catalog.

**Can two computers share one catalog?**
Not at the same time. Keep the catalog on one machine (or move it between machines, never using two copies in
parallel), because two diverging copies cannot be merged.

**Where are the logs?**
Commands print to the console only. The agent logs to `%LOCALAPPDATA%\bdxl\agent\agent.log`.

**Which version do I have?**
`bdxl version`: the version, the commit it was built from and when. Include its output when reporting a problem. A
disc's `disc.json` names the build that wrote it (`createdBy`), and `bdxl discs` shows it for every disc.

**`The folder would be wiped after the import, but …`** (`catalog import`)
`catalog import` wipes the plain folder afterwards, and refuses a folder that holds more than an export (subfolders,
links, `bdxl.key` or `.enc` files) before importing anything. Import from the folder `catalog export` made, or add
`--keep`.

**`… is compressed with 'xyz', which this bdxl does not know`**
That copy was made by a newer bdxl with another compression codec. Restore it with that version (or a newer one).

**How do I start completely over?**
Delete the catalog folder (and staging folders). Burned discs stay readable with their password; `scandisc` brings
them into a new catalog made with `init --key-from <disc>`.
