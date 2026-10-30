# Catalog Mirror

[Home](Home.md) › Catalog Mirror

The catalog is the only place that knows which file is on which disc. A **mirror** is a second, always current copy
of it in another folder, ideally on another drive (a second disk, a NAS share). Every save writes both. As long as a
mirror is set, nothing that changes the catalog runs unless the mirror is there and holds exactly the same.

## Setting it up

```
bdxl settings set mirror F:\bdxl-mirror
```

The folder must be empty (it gets a copy at once) or already hold this catalog (it is then only checked). It may not
be inside the catalog folder. `bdxl settings` shows it and whether it is in step; `bdxl catalog mirror status` too.

The mirror holds the encrypted catalog files, `catalog.hash.json` and `bdxl.key`, **no history**. It is a complete
catalog folder in its own right: `bdxl status --catalog F:\bdxl-mirror` opens it. (Do not work on it that way; it
would no longer be in step.)

## What happens when

| Situation | Commands that change the catalog | Read-only commands (`status`, `find`, `plan`, `restore`, `… list`, `catalog check` …) |
|---|---|---|
| mirror in step | run; every save updates the mirror too | run |
| mirror drive not connected | refused (exit code 4): connect it | run, with a warning |
| mirror behind (a save did not reach it) | refused: `bdxl catalog mirror sync` | run, with a warning |
| mirror damaged | refused: `bdxl catalog mirror sync` | run, with a warning |
| the catalog damaged, the mirror fine | everything stops (exit code 4); the message points to `catalog mirror restore` | |
| the mirror newer than the catalog | refused; the message says so | run, with a warning |

`password change` and `recovery-key` also need the mirror (`bdxl.key` changes there too).

## Steps

**The mirror drive was not connected, or a save did not reach it**

```
bdxl catalog mirror status     # what is wrong
bdxl catalog mirror sync       # copy the catalog over the mirror; asks first if the mirror is the newer one
```

**The catalog is damaged (or was lost) and the mirror is fine**

```
bdxl catalog mirror restore    # the password of the mirror's bdxl.key; --recovery for the recovery key
```

The mirror's catalog is checked with its own `bdxl.key` first; the damaged catalog goes to `history\`, then the
mirror's files (and its `bdxl.key`) are copied in. If the catalog folder is gone entirely, copy the mirror folder to the
catalog's place (it is a complete catalog), then set the mirror again.

**The catalog is behind the mirror** (for example a catalog folder copied back from an old backup): the same
`catalog mirror restore` takes the newer version.

**Stop mirroring**

```
bdxl settings set mirror none   # works also while the mirror is not connected; its files stay where they are
```

## Good to know

- A save that cannot reach the mirror (the drive was pulled during the command) still saves the catalog and then
  stops with an error; the next command asks for `catalog mirror sync`.
- Every command reads the whole mirror once to check it, like the catalog itself. On a slow network share that
  adds time per command (a catalog of 1 million files is roughly 100-150 MB).
- The mirror's location is kept in `mirror.json` in the catalog folder (plain JSON, `{"path": "F:\bdxl-mirror"}`),
  so that `password change` and a damaged catalog still know it.
- The mirror complements catalog backups (`stage --with-catalog`, `catalog backup create`); it does not replace
  them: a mistake saved in the catalog is saved in the mirror too. `catalog history` / `catalog rollback` undo those.
