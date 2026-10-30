# bdxl – encrypted catalog and discs (design, agreed 2026-10-30)

bdxl moves towards kopia's model: the catalog is an encrypted "repository", every staged disc is encrypted, and one
password (changeable) protects everything. This file is the agreed design; `HANDOFF.md` says what is built so far.

## Decisions

| Topic | Decision |
|---|---|
| Keys | A random 256-bit **master key** per catalog encrypts everything. The password only unlocks it. |
| Password change | Re-wraps the master key; no data or catalog file is rewritten. |
| KDF | **Argon2id** (libsodium via NSec), deliberately expensive: `init` calibrates to ~2 s (about 1 GiB, 3-4 passes on a typical PC), at least 256 MiB unless overridden (with a warning). Parameters are stored, so they can be raised at any password change. |
| Recovery key | Optional, opt-in: 32 random bytes shown once, a second envelope of the master key. |
| Unlocked state | A background **agent** holds master keys in memory (never on disk), one per catalog id, with idle timeout + max lifetime, locked on Win+L / sleep, ends at sign-out. No cache file fallback. |
| Write-once media | Every disc carries the envelope current when it was staged. A password change does not revoke the old password (old password + old disc still opens the master key); newest password + newest disc opens everything. Accepted. |
| Key rotation | `password change --rotate-key` (added after phase 4): a new master key for everything new; older keys stay in a key ring so every disc stays readable. |
| Compression | Per alias (`off`/`auto`/`on` + level), zstd, per chunk inside the encrypted format; sizes counted during `scan` so planning stays exact. Later phase; the format reserves the flag now. |
| Old formats | Nothing was staged before this design, so `BDXLENC1` and the per-disc password scheme are removed, not migrated. The plaintext catalog is migrated. |

## Key structure

```
password ──Argon2id(salt, memory, passes)──► wrapping key ──AES-256-GCM──► master key   (password envelope)
recovery key ──HKDF-SHA256(salt)──────────► wrapping key ──AES-256-GCM──► master key   (recovery envelope, optional)

master key ──HKDF-SHA256(salt = object salt, info = "bdxl-enc2 " + purpose)──► key of one encrypted object
master key ──HKDF-SHA256(info = "bdxl catalog integrity")──────────────────► HMAC key of catalog.hash.json
```

Purposes: `catalog` (catalog files, history, backups), `manifest`, `file` (staged data).

### `bdxl.key` (plain JSON, safe to store openly)

```json
{
  "format": "bdxl-key-1",
  "catalogId": "<32 hex>",
  "keyId": "<16 hex>",
  "createdUtc": "...",
  "envelopes": [
    { "kind": "password", "kdf": "argon2id", "memoryKiB": 1048576, "passes": 3, "parallelism": 1,
      "salt": "<base64 16>", "nonce": "<base64 12>", "wrapped": "<base64 32+16>", "createdUtc": "..." },
    { "kind": "recovery", "kdf": "hkdf-sha256",
      "salt": "<base64 32>", "nonce": "<base64 12>", "wrapped": "<base64 32+16>", "createdUtc": "..." }
  ]
}
```

- The envelope's AAD is `bdxl-key-1|<catalogId>|<keyId>|<kind>`, so an envelope cannot be moved to another catalog.
- Written atomically (temp file, test that it opens, rename).
- Kept in the catalog folder, NOT in `history/` snapshots (a rollback must not bring back an old password), copied
  into every catalog backup and onto every disc.

### Recovery key text

32 bytes + 2 checksum bytes (start of SHA-256) in Crockford base32, groups of 5 characters separated by `-`.
Typing is forgiving (case, `O`→`0`, `I`/`L`→`1`, spaces); a typo fails the checksum before any decryption.

## Encrypted object format `BDXLENC2`

```
magic "BDXLENC2" (8) | key id (8) | object salt (32) | nonce prefix (8)
chunks: length (uint32) | AES-256-GCM ciphertext (length) | tag (16)
```

- Key of the object = HKDF(master key, object salt, purpose). Nonce = prefix (8) + chunk number (uint32).
- AAD = chunk number (uint32) + last-chunk flag (1 byte): reordering, cutting off or extending is detected.
- Each chunk's plaintext is `mode (1 byte) | data`, from 1 MiB of source data. Mode 0 = stored, 1 = zstd
  (phase 4). The mode is inside the encryption, so compressibility is not visible from the header.
- Overhead: 56 bytes + 21 bytes per chunk.
- The key id lets a wrong catalog/key be reported as such instead of as damage.

## Catalog folder

| File | Contents |
|---|---|
| `bdxl.key` | envelopes (above) |
| `files.NNN.jsonl.enc`, `discs.jsonl.enc`, `blanks.jsonl.enc`, `settings.json.enc` | the catalog, `BDXLENC2` (zstd-compressed), purpose `catalog`; plaintext never touches a disk. `files.jsonl.enc`: the unsplit file list of older catalogs, now a marker that older builds cannot parse |
| `catalog.hash.json` | SHA-256 of each encrypted file + key id + save counter + time, and an **HMAC** over all of it (key from the master key) |
| `history/<time>-<command>/` | encrypted copies + their `catalog.hash.json` (no `bdxl.key`) |

- Damage (hash mismatch) is visible without the password; tampering or files mixed from different saves fail the HMAC.
- Hand editing is gone: `catalog export <dir>` writes plaintext JSON where the user chooses, `catalog import <dir>`
  reads it back (after confirmation, previous state kept in history). `catalog accept` remains for re-recording a
  catalog whose files all decrypt but whose HMAC does not match.
- Catalog backups (`catalog backup create`, `_catalog` on discs) copy the encrypted files, `catalog.hash.json` and
  `bdxl.key`, plus Reed-Solomon repair data. Creating, verifying and repairing a backup need no password.
  `backup restore` into an existing catalog with the same catalog id keeps the current `bdxl.key` (newer password).

## Commands

| Command | |
|---|---|
| `init [--recovery-key] [--kdf-memory SIZE] [--kdf-passes N]` | create an encrypted catalog; on a folder with a plaintext catalog: migrate it |
| `password change [--recovery] [--kdf-memory] [--kdf-passes]` | new password (current one, or the recovery key with `--recovery`) |
| `recovery-key create` / `remove` | add or replace / remove the recovery envelope (burned discs keep theirs) |
| `unlock [--idle 30m] [--max 8h]`, `unlock --status`, `lock`, `lock --all` | the agent |
| `catalog export <dir>`, `catalog import <dir>` | plaintext copy out / back in |

Every other command needs the master key: from the agent if it holds this catalog, otherwise the password
(`BDXL_PASSWORD`, `--password-env VAR`, or typed). A command on a folder without a catalog says to run `init`; a
plaintext catalog is migrated after confirmation (same code as `init`).

## Migration of a plaintext catalog

1. Check the plaintext catalog (old SHA-256 hash file); a broken one is refused unless `--force`.
2. New password (typed twice, password rules), optional recovery key, create `bdxl.key`.
3. Write the encrypted files + `catalog.hash.json`, check they load, then delete the plaintext files.
4. Encrypt every `history/` snapshot the same way; a snapshot that failed its old hash check is marked as such.
5. Note to the user: on an SSD, deleted plaintext cannot be wiped reliably.

## Agent

- `unlock` copies the program folder to `%LOCALAPPDATA%\bdxl\agent\<build-id>\` and starts `bdxl agent` from there
  (rebuilds/updates never hit a locked `bdxl.exe`), outside the terminal's process group.
- Named pipe with `PipeOptions.CurrentUserOnly` (only this user; the client checks the server's owner). The agent
  refuses to start if the pipe already exists.
- Protocol: length-prefixed JSON messages with a protocol version; a different protocol version makes the client
  stop the old agent (the user unlocks again).
- Per catalog id: master key in pinned memory, idle timeout and max lifetime (defaults in settings), wiped on lock.
  Win+L and sleep lock everything (`Microsoft.Win32.SystemEvents`, each switchable). The agent exits when no key is
  left.
- `unlock --status`: catalogs, expiry times, agent pid and build. Log: `%LOCALAPPDATA%\bdxl\agent\agent.log`, never
  key material.
- Tests: `BDXL_AGENT=off`; the server runs in-process with its own pipe name; one test starts a real agent.

## Discs (phase 2)

- Files, manifest and `_catalog` use `BDXLENC2` with the master key (purposes `file`, `manifest`, `catalog`).
- Every disc gets `bdxl.key` (envelope current at staging), the catalog id and key id in `disc.json`, and a
  `README.txt` describing `bdxl.key` and `BDXLENC2` (Argon2id: RFC 9106; AES-256-GCM; HKDF: RFC 5869) so a
  decryptor can be written without bdxl.
- A lost catalog: `init --key-from <disc>` creates a new catalog with the disc's `bdxl.key` (password of that
  disc's day, or `--recovery`), so it has the same master key and catalog id and reads every disc of that catalog;
  then `catalog backup restore` the newest `_catalog`, or `scandisc` each disc. (Decided while building phase 2
  instead of a catalog-less `restore --mount`: every command keeps working on a catalog, and `scandisc`,
  `verify --deep` and `restore` need nothing special.) A newer disc's `bdxl.key` holds the newer password.
- A disc whose key id is not the catalog's is refused by `stage --append`, `restage`, `discjson`; `restore`
  ignores its mount; `scandisc` explains how to read it (`init --key-from`).
- `DiscRecord.KeyCheck`, `DiscPassword`, `KeyCache`, `BDXLENC1` are removed; a disc's key check is its key id.

## Compression (phase 4)

- Per alias: `off`, `auto` (skip known compressed extensions such as jpg/heic/mp4/mkv/zip/7z, and per chunk keep
  whatever does not shrink), `on`; plus a level. Global default + per alias override.
- zstd (RFC 8878), each 1 MiB chunk compressed on its own (mode byte above): damage stays within a chunk, parts
  start on chunk boundaries, incompressible data grows by 1 byte per chunk at most.
- `scan` compresses (and discards) to count the stored size of files in aliases with compression on, so the planner
  stays exact; a changed setting needs the sizes recounted. If a library update changes sizes, `stage` defers what no
  longer fits; the disc reserve absorbs small differences.
- Compression is recorded on the stored copy (`BackupRef`), not on the file, so dedup across aliases keeps working.
- Only in encrypted copies (every staged disc since phase 3).

## Phases

1. (done) Master key, `bdxl.key` (Argon2id), recovery key, `BDXLENC2`, encrypted catalog with HMAC integrity,
   migration, `init`, `password change`, `recovery-key`, `catalog export/import`, encrypted catalog backups, the agent.
2. (done) Discs use the master key (stage, restage, restore, verify, scandisc, discjson, `_catalog`); `bdxl.key`,
   ids and README on every encrypted disc; `init --key-from`; old per-disc scheme removed.
3. (done) Encrypted only: plain staging removed (`--encrypt` accepted and ignored); plain discs imported with
   `scandisc` stay readable, verifiable, restorable and restageable as they were, but take no new session.
4. (done) Compression per alias: `compression set <alias>|--default off|auto|on [--level N]`, default off. The
   level is recorded on each stored copy (`BackupRef.Compression`, manifest) so `restage` reproduces the size; counted
   sizes are tied to level + zstd library version and fall back to uncompressed sizes (so no overflow guard is needed).
5. (done) Key rotation, see below.

## Key rotation (`password change --rotate-key`)

- `bdxl.key` format `bdxl-key-2`: each envelope (`"ring": true`, `keyId` = the current key when sealed) encrypts a key
  ring: key id (8) + key (32) for every key, oldest first, the last one current. AAD `bdxl-key-2|catalog|keyId|kind`.
  `bdxl-key-1` files (one key) are read as rings of one; any write produces `bdxl-key-2`.
- Rotation: a new key (same catalog id) is added to the ring, a new password envelope is made, and a new recovery key
  if there was one (the old one could not wrap the new key); `bdxl.key` is written, then the catalog is saved under the
  new key. History versions and catalog backups keep the key they were made with; readers pick the key by id (file
  headers, `catalog.hash.json`'s key id, `DiscRecord.KeyId`), so a stop between the steps loses nothing.
- New discs, catalog saves and backups use the current key. `stage --append`, `restage` and `discjson` use the disc's
  own key, so every disc stays under one key. The agent holds the ring (protocol 2) and is locked after a rotation.
- Each disc carries the ring as of its staging, under that day's password: a leaked old password + an old disc opens
  only the keys up to then (that disc, older discs, their `_catalog` backups); the newest password + the newest disc
  opens everything. A burned disc cannot be re-keyed except by `restage` + burning it again.
