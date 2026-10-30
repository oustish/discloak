# Files and Formats

[Home](Home.md) › Files and Formats

Full design and cryptographic details: [`bdxl/ENCRYPTION.md`](../bdxl/ENCRYPTION.md).

## The catalog folder

```
<catalog>\
  bdxl.key                 the master key(s), wrapped by the password and (optionally) the recovery key - plain JSON
  files.000.jsonl.enc …    every file of every location, with its copies on discs        } encrypted with the
                           (up to 200,000 per file; one location never shares a file)   } current master key
  discs.jsonl.enc          every disc                                                     } (BDXLENC2,
  blanks.jsonl.enc         blank disc stock                                               }  purpose "catalog",
  settings.json.enc        settings, ignore rules, compression settings                   }  zstd-compressed)
  files.jsonl.enc          only a marker line that older bdxl versions cannot read (see below)
  catalog.hash.json        SHA-256 of each .enc file + key id + save counter + HMAC
  mirror.json              (optional) where the catalog mirror is: {"path": "F:\\bdxl-mirror"} - plain JSON
  history\<time>-<command>\   previous versions (.enc files + their catalog.hash.json, never bdxl.key)
```

Do not edit these files. Use `catalog export` / `catalog import` to read or change the content as plain JSON.
A save rewrites only the files whose content changed (a scan of one location touches only that location's files).

Catalogs from before the split kept the whole file list in `files.jsonl.enc`; they are read as they are and split on
the next save. From then on `files.jsonl.enc` holds a single line that is not JSON, so an **older bdxl refuses the
catalog** (exit code 4, `Cannot be read: files.jsonl line 1`) instead of seeing an empty file list. Use the current
version; an older one only works on a version rolled back from `history`.

`bdxl.key` is safe to keep openly, but without it (and without a disc or backup that has it) the catalog cannot be
opened. Back up the whole folder.

## A staged / burned disc

```
<disc>\
  disc.json                label, creation time, file count, size, sessions, catalog id, key id - plain JSON
  bdxl.key                 the catalog's key file as it was when the disc was staged - plain JSON
  README.txt               how to read the disc, with or without bdxl
  manifest.jsonl.enc       list of the disc's files: real path, size, hashes, parts, compression - encrypted
  data\000\000001.enc      the files, one encrypted file per original, names that say nothing (1000 per folder)
  data\000\000002.enc
  ...
  _catalog\                (stage --with-catalog) a catalog backup, see below
```

Without the password only `disc.json`, `bdxl.key` and `README.txt` can be read. File names, paths, hashes and
content are encrypted; what shows is the number of files and the size of each encrypted file.

### `disc.json`

```json
{
  "label": "BDXL-0001",
  "createdUtc": "2026-10-30T08:46:15Z",
  "fileCount": 1234,
  "usedBytes": 96000000000,
  "encrypted": true,
  "sessions": 2,
  "catalogId": "849CE29D5206E512059962B7D091C5E4",
  "keyId": "C4782A3F591D44A3"
}
```

`restore` and `verify` use it to recognise a disc in any drive. `keyId` names the master key the disc's files are
encrypted with.

### Discs burned without bdxl

Plain files in their folders. `scandisc` records them; `discjson` can add a `disc.json` (and a plain
`manifest.jsonl`) if the disc can still be written to.

## A catalog backup (`_catalog` or `catalog backup create`)

```
<backup>\
  files.NNN.jsonl.enc, files.jsonl.enc, discs.jsonl.enc, blanks.jsonl.enc, settings.json.enc, catalog.hash.json, bdxl.key
  <each>.par               Reed-Solomon parity blocks (25% by default)
  <each>.repair.json       block size, per-block SHA-256, parity layout
  README.txt
```

Checking and repairing need no password; restoring does.

## Encrypted files (`BDXLENC2`)

```
"BDXLENC2" (8) | key id (8) | salt (32) | nonce prefix (8) | chunks...
chunk: length (uint32 LE) | AES-256-GCM ciphertext | tag (16)
```

- The file's key = HKDF-SHA256(master key, salt, `"bdxl-enc2 " + purpose`), purpose `file`, `manifest` or `catalog`.
- Each chunk holds up to 1 MiB of data, preceded (inside the encryption) by a mode byte: 0 = stored, 1 = zstd.
- The chunk number and a last-chunk flag are authenticated: reordering, truncating or extending a file is detected.
- Size of an uncompressed copy: 56 bytes + data + 21 bytes per MiB.

## `bdxl.key`

```json
{
  "format": "bdxl-key-2",
  "catalogId": "…32 hex…",
  "keyId": "…16 hex… (the current master key)",
  "envelopes": [
    { "kind": "password", "kdf": "argon2id", "memoryKiB": 1048576, "passes": 6, "parallelism": 1,
      "salt": "…", "nonce": "…", "wrapped": "…", "ring": true, "keyId": "…" },
    { "kind": "recovery", "kdf": "hkdf-sha256", "salt": "…", "nonce": "…", "wrapped": "…", "ring": true, "keyId": "…" }
  ]
}
```

Each envelope holds the key ring (every master key so far) encrypted with a key derived from the password (Argon2id)
or the recovery key (HKDF). Older files (`bdxl-key-1`) hold a single key and are still read.

## Outside the catalog

| Path | |
|---|---|
| `%LOCALAPPDATA%\bdxl\agent\<build>\` | the copy of the program the agent runs from |
| `%LOCALAPPDATA%\bdxl\agent\agent.log` | the agent's log (start, stop, unlock, lock; never keys) |
