# Maintenance

[Home](Home.md) › Maintenance

## Rescanning

```
bdxl scan                      # every known location
bdxl scan D:\Photos            # one location
bdxl scan --threads 4          # hash with 4 threads (SSDs, several disks); default: settings default-threads
bdxl scan D:\Photos --full     # hash everything again, even files that look unchanged
```

- Unchanged files (same size and modified time) are not read again.
- New and changed files are hashed; changed ones become pending again.
- Files that disappeared are marked *missing* (they stay in the catalog with their disc copies).
- A missing file whose identical content shows up under another path is a **move**: its catalog entry moves to the
  new path with its disc copies; nothing is stored again.
- Ctrl+C stops cleanly: what was hashed is saved, nothing is marked missing in the interrupted location, and the
  same command continues later. The catalog is also saved every 2 minutes during long scans. Because nothing is
  marked missing, `purge` finds nothing to remove until a scan of that location runs to the end.
- A file that changes while it is being read (still being written to) is skipped with
  `it changed while it was being read`; its entry stays as it was and the next scan reads it again.
- Sizes come from the file itself, not from the folder listing: Windows can show an old size there for hard-linked
  files and files another program keeps open.

## Ignore rules

Rules use `.gitignore` syntax and apply to paths relative to each scanned folder:

```
bdxl ignore add "Thumbs.db" "*.tmp" "node_modules/" "/cache/**" "!keep.tmp"
bdxl ignore list
bdxl ignore test Photos/2020/Thumbs.db      # does a path match?
bdxl ignore remove "*.tmp"                  # or by its number from 'ignore list'
```

Ignored files stay in the catalog (marked ignored), are not hashed and are never staged. A new rule takes effect
at once (`ignore apply` repeats it). For files that are already backed up, no new copies are made, but `restage` of an
old disc still includes them.

## Renaming a location

```
bdxl alias                       # list locations: alias, file count, folder
bdxl alias rename Docs Documents
```

This affects the catalog and future discs. Burned discs keep the old folder name in their manifest; the catalog still
finds the files.

A location whose folder moved (another drive letter, another path): an alias belongs to one folder, so scan the new
folder under a new alias (`bdxl scan X:\Photos --name Photos2`). Files whose content is already on discs are
recognised by their hash and not stored again. If the old folder is still reachable but empty, rescanning it
(`bdxl scan`) turns the old entries into moves to the new location; once no entries are left under the old alias,
`bdxl alias rename` can give the new location the old name.

## Forgetting files that are gone

Files that disappeared from the source and were never put on a disc can be removed from the catalog:

```
bdxl purge --dry-run      # what would go
bdxl purge
```

Files with a copy on any disc are kept.

## Settings

```
bdxl settings                              # show all
bdxl settings set label-prefix ARCH-       # next disc: ARCH-0008 (the counter continues, never resets)
bdxl settings set currency EUR
bdxl settings set history-keep 20          # catalog versions kept in history\
bdxl settings set default-threads 4
bdxl settings set unlock-idle 30m          # see Security
bdxl settings set unlock-max 8h
bdxl settings set lock-on-win-l yes
bdxl settings set lock-on-sleep yes
```

## Blank disc stock

```
bdxl blanks                                # list, with usable space
bdxl blanks add 5 --size 128001769472 --reserve 400MB --cost 60
bdxl blanks reserve 500MB                  # change the reserve of all blanks (--size S: only those of that size)
bdxl blanks cost 39.99                     # change the price of all blanks (--size S)
bdxl blanks remove B0007                   # or: all
```

## Costs

```
bdxl cost report                           # per disc, per GB and per TB of data and of capacity; stock value
bdxl cost set BDXL-0002 42.50              # the price of one disc (also for discs bought earlier)
```

## Catalog versions (undo)

Before each command that changes the catalog, the previous version is kept in `history\`:

```
bdxl catalog history                       # versions, newest first, each checked
bdxl catalog rollback 20261030-091500-stage
```

A rollback first saves the current state, so it can itself be undone.

## Catalog backups

Besides `stage --with-catalog`, make a backup anywhere:

```
bdxl catalog backup create F:\safe                       # F:\safe\catalog-<date>\ with repair data
bdxl catalog backup verify F:\safe\catalog-20261030-1200 # check (no password needed)
bdxl catalog backup verify F:\safe\catalog-20261030-1200 --repair
```

Restoring one: [Disaster Recovery](Guide-Disaster-Recovery.md#the-catalog-is-damaged-or-fails-its-check).

## Reading or editing the catalog as plain JSON

The catalog is encrypted, so it cannot be edited by hand. To read or fix it:

```
bdxl catalog export D:\catalog-plain        # an empty folder; the files there are NOT encrypted
   ... read / edit files.jsonl, discs.jsonl, blanks.jsonl, settings.json ...
bdxl catalog import D:\catalog-plain        # replaces the catalog (the previous version is kept in history),
                                            # then wipes D:\catalog-plain (--keep leaves it)
```

The import wipes the plain folder once the new catalog is saved: every file in it (also an editor's backup copy
there) is overwritten with random bytes and deleted, and the folder removed. What that cannot reach: an SSD may keep
the old blocks until it reclaims them, and shadow copies, synced folders (OneDrive), NAS snapshots and editors' own
backups elsewhere keep copies of their own - Windows 11 Notepad, for one, keeps the tabs it had open. So export onto
an encrypted drive (BitLocker) if you can, and edit with an editor that keeps nothing (or close the file's tab
first).

## Updating bdxl

Publish again (`dotnet publish -p:PublishProfile=win-x64`) and copy the new `bdxl.exe` over the old one. Catalogs,
discs and key files of older versions are read as they are. A running agent keeps working; if the new version talks
to it differently, the next `unlock` replaces it (unlock again then).

Do not go back to an older version afterwards: a catalog saved by a newer version may be in a newer layout, which
an older one refuses (exit code 4) rather than misreads. See
[Files and Formats](Files-and-Formats.md#the-catalog-folder).
