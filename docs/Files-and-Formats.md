# Files and Formats

[Home](Home.md) › Files and Formats

Full design and cryptographic details: [`discloak/ENCRYPTION.md`](../discloak/ENCRYPTION.md).

## The catalog folder

```
<catalog>\
  discloak.key             the master key(s), wrapped by the password and (optionally) the recovery key - plain JSON
                           (bdxl.key in a catalog from before 0.8.0, renamed on its next save)
  files.000.jsonl.enc …    every file of every location, with its copies on discs        } encrypted with the
                           (up to 200,000 per file; one location never shares a file)   } current master key
  discs.jsonl.enc          every disc                                                     } (DCLKENC2,
  blanks.jsonl.enc         blank disc stock                                               }  purpose "catalog",
  settings.json.enc        settings, ignore rules, compression settings                   }  zstd-compressed)
  files.jsonl.enc          only a marker line that older discloak versions cannot read (see below)
  catalog.hash.json        SHA-256 of each .enc file + key id + save counter + HMAC
  mirror.json              (optional) where the catalog mirror is: {"path": "F:\\discloak-mirror"} - plain JSON
  history\<time>-<command>\   previous versions (.enc files + their catalog.hash.json, never discloak.key)
```

Do not edit these files. Use `catalog export` / `catalog import` to read or change the content as plain JSON.
A save rewrites only the files whose content changed (a scan of one location touches only that location's files).

Catalogs from before the split kept the whole file list in `files.jsonl.enc`; they are read as they are and split on
the next save. From then on `files.jsonl.enc` holds a single line that is not JSON, so an **older discloak refuses the
catalog** (exit code 4, `Cannot be read: files.jsonl line 1`) instead of seeing an empty file list. Use the current
version; an older one only works on a version rolled back from `history`.

`discloak.key` is safe to keep openly, but without it (and without a disc or backup that has it) the catalog cannot be
opened. Back up the whole folder.

## A staged / burned disc

```
<disc>\
  disc.json                label, creation time, sessions, format, catalog id, key id, the discloak build that wrote it -
                           plain JSON, nothing about the files
  discloak.key             the catalog's key file as it was when the disc was staged - plain JSON (bdxl.key on a disc
                           staged before 0.8.0; a disc with sessions from before and after holds both)
  README.txt               how to read the disc, with or without discloak
  packs.sha256             SHA-256 of every pack (sha256sum format) - plain
  manifest.enc             which file lies where in the data: real path, position, size, hashes, parts, compression -
                           encrypted, padded to whole 64 MiB
  manifest.s2.enc …        (appended sessions) the files each session added - all manifests together list the disc
  data\000\000001.pack     the data: every file laid end to end in one stream, cut into packs of 64 MiB, each pack
  data\000\000002.pack     encrypted on its own; all packs have the same size (the last one is filled up with zeros)
  ...                      1000 per folder
  _catalog\                (stage --with-catalog) a catalog backup, see below
```

Without the password only `disc.json`, `packs.sha256`, `discloak.key` and `README.txt` can be read. File names, paths,
sizes, hashes, the number of files and the content are all encrypted. What shows is the number of packs (the amount
of data, to 64 MiB), the dates and the number of sessions. A disc of 300 small files and a disc of one large file with
the same amount of data look exactly alike. Even the manifest is padded to whole 64 MiB, so its size does not give away the
number of files.

A **pack** is 67,109,944 bytes: `"DCLKPAK1"` (8) | key id (8) | salt (32) | nonce prefix (8) | 64 × (1 MiB
ciphertext + 16-byte tag). Each 1 MiB chunk is authenticated with the pack's number and its own, so a pack or chunk
moved to another place does not decrypt. Byte X of the data is in pack X / 64 MiB + 1 - reading one file decrypts
only the chunks it lies in, and a damaged spot costs only the files with data in those chunks.

A **compressed** file (see [Compression](Guide-Compression.md)) is stored as zstd frames, one per MiB of the file: a
standard zstd stream that `zstd -d` reads.

### `disc.json`

```json
{
  "label": "DISC-0001",
  "createdUtc": "2026-10-30T08:46:15Z",
  "format": "discloak-pack-1",
  "sessions": 2,
  "catalogId": "849CE29D5206E512059962B7D091C5E4",
  "keyId": "C4782A3F591D44A3",
  "packSize": 67108864,
  "chunkSize": 1048576,
  "createdBy": "discloak 0.8.0+3f2a1c9.20261104T1200Z"
}
```

`restore` and `verify` use it to recognise a disc in any drive. `keyId` names the master key the disc's packs are
encrypted with. `createdBy` is the discloak build that wrote the disc's latest session (see `discloak version`; discs staged
before 0.6.0 have none); the catalog keeps it for every session. How many files a disc holds and how big they are is only in the catalog and in the encrypted
manifest.

### The manifest (`manifest.enc`, `manifest.sN.enc`)

JSON lines, encrypted like the catalog's files (`DCLKENC2`, purpose `manifest`), followed by empty lines up to a
whole number of 64 MiB:

```json
{"source":"Photos/2024/IMG_0001.CR3","at":0,"size":25165824,"plainSize":25165824,"sha256":"…","modified":"2024-06-01T10:00:00Z"}
{"source":"Docs/ledger.csv","at":25165824,"size":1203345,"plainSize":6400000,"sha256":"…","modified":"…","compression":3,"codec":"zstd"}
```

`at`/`size`: where the stored bytes lie in the disc's data and how many there are; `plainSize`/`sha256`: the original.
`compression`/`codec`: the level and the codec of a compressed copy (only `zstd` so far; a compressed copy without
`codec`, staged before 0.6.0, is zstd too).
A part of a split file adds `part`, `parts`, `offset` (where the part belongs), `fileSize` and `fileSha256`.

discloak reads only discs it made itself. Discs written with other tools cannot be recorded in a catalog.

## A catalog backup (`_catalog` or `catalog backup create`)

```
<backup>\
  files.NNN.jsonl.enc, files.jsonl.enc, discs.jsonl.enc, blanks.jsonl.enc, settings.json.enc, catalog.hash.json, discloak.key
  <each>.par               Reed-Solomon parity blocks (25% by default)
  <each>.repair.json       block size, per-block SHA-256, parity layout
  README.txt
```

Checking and repairing need no password; restoring does.

## Encrypted files (`DCLKENC2`)

```
"DCLKENC2" (8) | key id (8) | salt (32) | nonce prefix (8) | chunks...
chunk: length (uint32 LE) | AES-256-GCM ciphertext | tag (16)
```

Used for the catalog's files and a disc's manifests (a disc's data is in packs, above).

- The file's key = HKDF-SHA256(master key, salt, `"bdxl-enc2 " + purpose`), purpose `manifest` or `catalog`
  (`pack` for a pack). The label keeps the program's old name: it is part of the cryptography (see below).
- Each chunk holds up to 1 MiB of data, preceded (inside the encryption) by a mode byte: 0 = stored, 1 = zstd.
- The chunk number and a last-chunk flag are authenticated: reordering, truncating or extending a file is detected.
- Size of an uncompressed copy: 56 bytes + data + 21 bytes per MiB.

## `discloak.key`

```json
{
  "format": "discloak-key-2",
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

## Names before 0.8.0

Discloak was called **bdxl** before version 0.8.0. What was written then carries the old names, and is read as exactly
the same format, forever (a burned disc can never change); new files get the new names:

| Today | Before 0.8.0 | Where |
|---|---|---|
| `discloak.key` | `bdxl.key` | the key file: catalog, discs, catalog backups, mirror (a catalog's is renamed on its next save) |
| `discloak-key-2` | `bdxl-key-2` (and `bdxl-key-1`) | the key file's `"format"` |
| `discloak-pack-1` | `bdxl-pack-1` | `disc.json` `"format"` |
| `DCLKPAK1` | `BDXLPAK1` | the first 8 bytes of a pack |
| `DCLKENC2` | `BDXLENC2` | the first 8 bytes of the catalog's files and of manifests |

A disc can hold both: a disc staged by bdxl, with a session appended by Discloak, has old and new packs, `manifest.enc`
(old) and `manifest.s2.enc` (new), and both key files (`discloak.key` is the newer).

Some labels **inside the cryptography** keep the old name for good, on old and new files alike: they are inputs to
the key derivation and authentication, and changing them would make everything written so far unreadable. They are
`"bdxl-enc2 " + purpose` (the key of a pack, manifest or catalog file), `"bdxl recovery key"` (the recovery key's
wrapping key), `"bdxl catalog integrity"` and `"bdxl-catalog-integrity-1"` (the catalog's HMAC), and
`bdxl-key-2|…` / `bdxl-key-1|…` (the authenticated data of a key envelope).

## Outside the catalog

| Path | |
|---|---|
| `%LOCALAPPDATA%\discloak\agent\<build>\` | the copy of the program the agent runs from |
| `%LOCALAPPDATA%\discloak\agent\agent.log` | the agent's log (start, stop, unlock, lock; never keys) |
