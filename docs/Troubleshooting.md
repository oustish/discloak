# Troubleshooting and FAQ

[Home](Home.md) › Troubleshooting

## Exit codes

| Code | Meaning |
|---|---|
| 0 | done |
| 1 | error (wrong arguments, wrong password, refused, nothing to do where something was expected) |
| 2 | partial: not everything fits (`plan`, `stage`), more discs needed (`restore`), data lost (`lost`) |
| 3 | problems found: corrupt or missing files (`verify`, `restore`), an unrecoverable backup file |
| 4 | the catalog failed its integrity check; only `catalog …` commands run |
| 130 | stopped with Ctrl+C (after cleaning up; see the command's message) |

## Common messages

| Message | What to do |
|---|---|
| `No catalog in … Create one with: bdxl init` | Wrong folder? Pass `--catalog <dir>`. Otherwise `bdxl init`. |
| `The catalog is encrypted: set the environment variable BDXL_PASSWORD, or run this in an interactive terminal.` | The command runs with redirected input (a script, a pipe). Set `BDXL_PASSWORD`, `bdxl unlock` first, or run it in a terminal. |
| `The password in BDXL_PASSWORD does not open the catalog …` | The variable holds an old or wrong password. |
| `Wrong password (3 attempts).` | See [Forgotten password](Guide-Disaster-Recovery.md#forgotten-password). |
| `Confirmation needed but input is redirected; add --yes.` | Add `--yes` in scripts. |
| `The catalog in … failed its integrity check, so no work is done` (exit 4) | [The catalog is damaged](Guide-Disaster-Recovery.md#the-catalog-is-damaged-or-fails-its-check). |
| `No blank discs are registered.` | `bdxl blanks add …` |
| `Nothing staged: no blank disc would be full enough (see --min-fill).` | Lower or drop `--min-fill`, or wait for more data. |
| `Nothing staged: the pending files do not fit on any registered blank disc` | A file is larger than every disc: `--split`, or larger blanks. |
| `changed since last scan - not staged. Re-run 'scan'.` | The source changed after the scan. `bdxl scan`, then stage again. |
| `WARNING: … folder is … more than the disc holds` | Raise the reserve (`bdxl blanks reserve …`), `discard` the disc, stage again. |
| `Folder already exists: …` | A staging folder of that label is still there. Delete or move it. |
| `… is not burned yet. To put more on it: 'discard …' and stage again.` | `--append` works only on burned discs. |
| `Session N of … is staged but not burned yet.` | Burn it and run `burned`, or `discard` it. |
| `… is not encrypted; bdxl only adds encrypted data to discs` | A plain disc from before bdxl cannot take a session. |
| `… encrypted with another key` / `This disc belongs to another catalog.` | The disc was made by another catalog (or another key). Use that catalog, or `init --key-from <disc>` into a new folder. |
| `Ignoring G:\: no disc.json there` | The disc has no `disc.json`: add `--disc <label>` (one mount at a time). |
| `A different folder is already catalogued as 'X'. Use --name <alias>.` | Each alias belongs to one folder; choose another name. |
| `This folder is already catalogued as 'X'.` | Rescan without `--name`, or rename with `bdxl alias rename`. |
| `The agent did not start (see …agent.log)` | Look at the log. Commands keep working with the password; `BDXL_AGENT=off` skips the agent. |
| `Windows did not let the agent leave this terminal's process group` | Closing that terminal ends the agent (and locks). Harmless. |

## FAQ

**Can a disc be read without bdxl?**
Yes. Its `README.txt` describes `bdxl.key` and the file format with standard algorithms (Argon2id, HKDF-SHA256,
AES-256-GCM, zstd), enough to write a decryptor. You need the password valid when the disc was staged (or the
recovery key of that time).

**What if I lose `bdxl.key`?**
It is in every catalog backup and on every encrypted disc. Restore a backup, or `init --key-from <disc>`.

**Is the password stored anywhere?**
No. Only `bdxl.key`, which holds the master key encrypted with a key derived from the password. With `unlock`, the
master key (not the password) is held in the agent's memory until it locks.

**Why does each command take a second or two before it starts?**
Deriving the key from the password is deliberately expensive (Argon2id, ~2 s), so guessing passwords is slow too.
`bdxl unlock` makes it a one-off.

**Why did a `scan` read files that had not changed?**
Their location's compression setting changed (or `--full` was given); their compressed size is counted again.

**How big should the reserve be?**
Enough for the file system (a few tens of MB on a 100 GB disc), the disc's own files (manifest ~300 bytes per file)
and, with `--with-catalog`, the catalog backup (~1.3× the catalog's `.enc` files). `stage` warns when it does not fit.

**Which file system should I burn with?**
UDF 2.50 (the usual choice for BD-R). bdxl's file names are short and plain ASCII, so any burning program works.

**Can I use DVDs, other discs, or folders on hard disks?**
Yes. Register blanks with their size; `verify`, `restore` and `scandisc` work on any folder laid out like a disc.

**Can two computers share one catalog?**
Not at the same time. Keep the catalog on one machine (or move it between machines, never using two copies in
parallel), because two diverging copies cannot be merged.

**Where are the logs?**
Commands print to the console only. The agent logs to `%LOCALAPPDATA%\bdxl\agent\agent.log`.

**How do I start completely over?**
Delete the catalog folder (and staging folders). Burned discs stay readable with their password; `scandisc` brings
them into a new catalog made with `init --key-from <disc>`.
