# Command Reference

[Home](Home.md) › Command Reference

`bdxl help` lists the commands; `bdxl <command> --help` shows the details of one.

**Options for every command**

| Option | |
|---|---|
| `--catalog <dir>` | catalog folder (default `.\catalog`) |
| `--password-env VAR` | take the password from this environment variable (default `BDXL_PASSWORD`) |
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
bdxl init [--recovery-key] [--kdf-memory <size>] [--kdf-passes N] [--force]
bdxl init --key-from <disc or folder> [--recovery]
```
| Option | |
|---|---|
| `--recovery-key` | also create a recovery key (shown once) |
| `--kdf-memory`, `--kdf-passes` | Argon2id settings; by default measured so unlocking takes ~2 s (below 256 MiB: warning) |
| `--force` | encrypt an old plaintext catalog even if it fails its own integrity check |
| `--key-from <path>` | a new, empty catalog with the master key(s) from `bdxl.key` in that folder (a disc, a catalog backup) |
| `--recovery` | with `--key-from`: unlock it with the recovery key instead of the password |

### scan
Index files: a folder, or every known location.
```
bdxl scan [<folder>] [--name <alias>] [--threads N] [--full]
```
| Option | |
|---|---|
| `<folder>` | omitted: rescan all known locations (unreachable ones are skipped) |
| `--name <alias>` | the location's name in the catalog and on discs (default: folder name) |
| `--threads N` | hash with N threads (default: setting `default-threads`) |
| `--full` | hash every file again, even unchanged ones |

Unchanged files (same size + time) are not read again; missing files are marked missing; moved files are
recognised by hash. Ignored files are catalogued but not hashed. With compression on, also counts compressed sizes.
Ctrl+C stops cleanly (exit 130).

### compression
Compression per location.
```
bdxl compression [list]
bdxl compression set <alias> off|auto|on [--level N]
bdxl compression set --default off|auto|on [--level N]
bdxl compression clear <alias>
```
`auto` skips already-compressed formats; level 1–19 (default 3). After a change run `scan`. See [Compression](Guide-Compression.md).

### plan
Which pending files would go on which blank disc. Changes nothing.
```
bdxl plan [--split] [--append <label> [--mount <path>]]
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
bdxl blanks [list]
bdxl blanks add <count> [--size S] [--reserve R] [--cost P]
bdxl blanks reserve <R> [--size S]
bdxl blanks cost <P> [--size S]
bdxl blanks remove <id|all> [--size S]
```
Default size: 96,882,130,944 bytes (BD-R XL 100 GB). `--size` on `reserve`/`cost`/`remove`: only blanks of that size.

### ignore
Ignore rules (`.gitignore` syntax), relative to each scanned folder.
```
bdxl ignore [list]
bdxl ignore add <pattern>...
bdxl ignore remove <pattern|number>...
bdxl ignore test <path>
bdxl ignore apply
```

---

## Making discs

### stage
Write the next disc folder(s), ready to burn.
```
bdxl stage <destDir> [--max-discs N] [--min-fill 0.9] [--split] [--with-catalog] [--append <label> [--mount <path>]]
```
| Option | |
|---|---|
| `--max-discs N` | stage up to N discs (default 1) |
| `--min-fill 0.9` | only stage discs at least 90% full |
| `--split` | cut files larger than every blank disc into parts |
| `--with-catalog` | add a catalog backup with repair data (`_catalog\`) |
| `--append <label>` | a new session for a burned, not finalized disc ([Appending](Guide-Appending.md)) |
| `--mount <path>` | with `--append`: the disc is in the drive |

Folder: `<destDir>\<label>\` (`<label>.s2` … for sessions) holding `data\NNN\NNNNNN.enc`, `manifest.jsonl.enc`,
`disc.json`, `bdxl.key`, `README.txt`, optional `_catalog\`. Ctrl+C finishes the disc with what was copied.

### burned
Mark a staged disc, or its staged session, as burned.
```
bdxl burned <label> [--date yyyy-MM-dd]
```

### verify
Re-read a disc and compare it with the catalog.
```
bdxl verify <label> <mountedDiscPath> [--deep]
```
`--deep` also decrypts each file and compares the content. Exit code 3: corrupt or missing files.

### discard
Forget a staged disc (or a staged session) that was never burned. Its files are pending again; its blank disc goes
back to stock. The folder is not deleted.
```
bdxl discard <label>
```

---

## Existing discs

### scandisc
Record a disc that is already burned.
```
bdxl scandisc <mountPath> [--label L] [--method auto|manifest|hash|quick] [--date D] [--capacity S] [--cost P]
              [--update] [--dry-run] [--write-disc-json] [--overwrite]
```
| Option | |
|---|---|
| `--label L` | its label (otherwise from `disc.json`, then the volume label) |
| `--method` | `manifest` (trust the disc's manifest), `hash` (read everything), `quick` (path+size+time), `auto` (default) |
| `--date`, `--capacity`, `--cost` | facts about the disc |
| `--update` | merge into a disc already in the catalog |
| `--dry-run` | show, change nothing |
| `--write-disc-json` | write `disc.json` (+ manifest) onto the disc if it can still be written (`--overwrite` replaces one) |

### discjson
Write `disc.json` (and a manifest, `bdxl.key`, `README.txt` if missing) onto a disc that can still be written to.
```
bdxl discjson <label> <mountPath> [--overwrite]
```

---

## Looking things up

### status
Sources, pending data, blank discs, discs; warns about files that exist nowhere any more.
```
bdxl status
```

### find
Where a file is backed up: part of a path, or the start of a SHA-256.
```
bdxl find <text|hashPrefix|*>
```

### discs
Every disc: state, encryption, sessions, cost, notes, free space.
```
bdxl discs
```

### cost
```
bdxl cost [report]
bdxl cost set <label> <amount>
```
Cost per disc, per GB and per TB of data and of capacity, and the value of blank stock (GB = 10^9 bytes).

---

## Recovery

### lost / destroyed / found
```
bdxl lost <label> [--reason text]          # may turn up again
bdxl destroyed <label> [--reason text]     # final
bdxl found <label>                          # a lost disc turned up
```
Copies on a lost/destroyed disc stop counting; files still on their source are pending again; files that exist
nowhere else are listed as DATA LOST (exit code 2).

### restage
Rebuild a disc's folder (same label, same files) to burn it again.
```
bdxl restage <label> <destDir> [--from dirA;dirB] [--allow-partial]
```
| Option | |
|---|---|
| `--from` | folders holding copies of the disc's stored files (e.g. copied off the damaged disc, or a `.partial` folder) |
| `--allow-partial` | stage even if some files cannot be rebuilt (they are dropped from the disc's record) |

The disc becomes *staged* until `burned`. Ctrl+C keeps `<label>.partial` for `--from`.

### restore
Copy files back from mounted discs.
```
bdxl restore <destDir> <text|hashPrefix|*> --mount F:\;G:\ [--disc label] [--source alias] [--flat] [--overwrite]
```
| Option | |
|---|---|
| `--mount` | drive(s)/folder(s) holding discs, separated by `;` |
| `--disc label` | the single mount has no `disc.json`: which disc it is |
| `--source alias` | only files of this location |
| `--flat` | no alias/path folders |
| `--overwrite` | replace existing files |

Exit code 2: more discs needed (listed); 3: failures.

---

## The catalog itself

### catalog
```
bdxl catalog check
bdxl catalog accept
bdxl catalog history
bdxl catalog rollback <id> [--force]
bdxl catalog export <dir>
bdxl catalog import <dir>
bdxl catalog backup create <dir> [--parity-percent 25]
bdxl catalog backup verify <dir> [--repair]
bdxl catalog backup restore <dir> [--force] [--recovery]
```
| Sub-command | |
|---|---|
| `check` | verify the catalog only (exit 4 if broken) |
| `accept` | every file still decrypts but the hash list does not match: record it anew |
| `history` | older versions, newest first, each checked |
| `rollback <id>` | go back to a version (current one saved first); `--force` for a damaged version |
| `export <dir>` | write plain JSON into an empty folder (**not encrypted**) |
| `import <dir>` | replace the catalog with plain JSON from such a folder |
| `backup create <dir>` | encrypted catalog + `bdxl.key` + Reed-Solomon repair data into `<dir>\catalog-<date>` |
| `backup verify <dir>` | check (and `--repair`) a backup, no password needed |
| `backup restore <dir>` | into an empty folder: with the backup's key (password or `--recovery`); into the same catalog: `--force`, current password stays; another catalog: refused |

### password
```
bdxl password change [--rotate-key] [--recovery] [--new-password-env VAR] [--kdf-memory <size>] [--kdf-passes N]
bdxl password check [<folder>] [--recovery]
```
| Option | |
|---|---|
| `--rotate-key` | also start a new master key (older keys kept in the key ring; recovery key replaced) |
| `--recovery` | `change`: unlock with the recovery key (`BDXL_RECOVERY_KEY` or typed) instead of the password; `check`: check the recovery key |
| `--new-password-env VAR` | the new password from this variable (default `BDXL_NEW_PASSWORD`); else typed twice, the second time copied from where you saved it, then confirmed |
| `check [<folder>]` | does this password open the `bdxl.key` of the catalog or of `<folder>` (a disc, a backup)? Shows when that password was set. Exit 0 = yes, 1 = no. Changes nothing, never uses the agent |

### recovery-key
```
bdxl recovery-key create      # make one (replaces an existing one)
bdxl recovery-key remove
```

### unlock / lock
```
bdxl unlock [--idle 30m] [--max 8h]
bdxl unlock --status
bdxl lock [--all]
```
`BDXL_AGENT=off` keeps a command away from the agent. See [Security](Guide-Security.md#unlocking-for-a-while).

### purge
Remove entries of files that are gone from their source and were never on a disc.
```
bdxl purge [--dry-run] [--yes]
```

### alias
```
bdxl alias [list]
bdxl alias rename <old> <new>
```

### settings
```
bdxl settings [show]
bdxl settings set <key> <value>
```
| Key | Default | |
|---|---|---|
| `label-prefix` | `BDXL-` | new discs: `<prefix><counter>`; the counter never resets |
| `currency` | | shown next to costs |
| `history-keep` | 10 | catalog versions kept in `history\` |
| `default-threads` | 1 | default for `scan --threads` |
| `unlock-idle` | 30m | an unlocked catalog locks after this long without use |
| `unlock-max` | 8h | … and after this long at the latest |
| `lock-on-win-l` | yes | lock when Windows is locked |
| `lock-on-sleep` | yes | lock when the computer goes to sleep |

### workflow / help
```
bdxl workflow          # a sample session (prints commands, runs nothing)
bdxl help [<command>]
```

## Environment variables

| Variable | |
|---|---|
| `BDXL_PASSWORD` | the catalog password (or name another variable with `--password-env`); for `init`, the new password |
| `BDXL_NEW_PASSWORD` | the new password for `password change` (or name another variable with `--new-password-env`) |
| `BDXL_RECOVERY_KEY` | the recovery key for `--recovery` |
| `BDXL_AGENT=off` | do not use the agent |
