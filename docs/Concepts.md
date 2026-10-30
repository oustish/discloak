# Concepts

[Home](Home.md) › Concepts

## Catalog

The catalog is the folder (`--catalog`, default `.\catalog`) where bdxl keeps everything it knows:

- every file of every scanned location (path, size, time, SHA-256 hash);
- every disc and which file copies it holds;
- the blank discs you own;
- settings, ignore rules and compression settings.

It is **encrypted** with the catalog's master key and protected against damage and tampering: every load checks
a hash list with an HMAC (`catalog.hash.json`). If anything does not check out, commands refuse to run until
you fix it (see [Disaster Recovery](Guide-Disaster-Recovery.md#the-catalog-is-damaged-or-fails-its-check)).

Before each command that changes the catalog, the previous version is kept in `history\` (10 versions by
default), so any change can be undone with `catalog rollback`.

## Locations and aliases

A *location* is a folder you scanned. Its *alias* is the name it has in the catalog (`scan --name`, default
the folder name), e.g. `Photos`. A file is identified as `alias/relative path`, e.g. `Photos/2024/IMG_0001.jpg`;
restored files go to `<destination>\<alias>\<path>`. `bdxl alias` lists them; `alias rename` renames one.

## Pending files and deduplication

A file is **pending** when it is on its source, not ignored, hashed, and no *live* disc (burned or staged, not
lost or destroyed) holds a complete copy of its content. Content is recognised by hash, so:

- identical files (in one or several locations) are stored once; the others are linked to that copy;
- a file whose content is already on a disc is not stored again, even under another name;
- a changed file is pending again; the older version stays recorded on its disc.

`bdxl status` and `bdxl plan` show what is pending.

## Discs, labels and states

Each staged disc gets a label `<prefix><counter>`, e.g. `BDXL-0007`. The counter only ever goes up. States:

| State | Meaning |
|---|---|
| staged | its folder was written, it is not burned yet (`burned` or `discard` next) |
| burned | recorded as burned; its copies count as backups |
| lost | missing; its copies do not count, files still on the source are pending again; `found` undoes it |
| destroyed | gone for good (final) |

A burned disc that is not finalized can take more data in further *sessions* ([Appending](Guide-Appending.md)).

## Blank discs and reserve

`bdxl blanks` is your stock of blank discs: size, reserve, price. Staging uses one up; `discard` gives it back.

<a id="disc-space"></a>**Space on a disc** is counted the way the disc's file system (UDF) uses it: every file ends on a
2 KiB sector and has a file entry (one more sector) and an entry in its folder's list. So a file takes its encrypted
size rounded up to 2 KiB, plus about 2.1 KB. For large files that is nothing; for 100,000 small files it is about
300 MB, and `plan`, `stage` and `--append` count it, so a disc full of small files still burns. (`plan` shows these
figures: a disc of small files holds fewer bytes than its size.)

<a id="reserve"></a>The **reserve** is space kept free on each disc. It has to hold the file system's own fixed overhead
(volume descriptors, the metadata partition) and the disc's own files: `disc.json`, `bdxl.key`, `README.txt`, the encrypted manifest (roughly 300 bytes per file)
and, with `--with-catalog`, the catalog backup (about 1.3× the size of the catalog's `.enc` files, because of the
repair data). If those come out larger than the reserve, `stage` warns that the folder will not fit. Raise it with
`bdxl blanks reserve 500MB`.

## Encryption, the password and the master key

- A random **master key** encrypts the catalog and everything on discs (AES-256-GCM, one encrypted file per
  original file, under hidden names such as `data/000/000001.enc`).
- The **password** only unlocks the master key, which is stored wrapped in `bdxl.key` (Argon2id key derivation).
  Changing the password rewrites `bdxl.key` and nothing else.
- The optional **recovery key** is a second way to unlock it, for a lost password.
- Every encrypted disc carries the `bdxl.key` of its day. A disc can be read with the catalog, or on its own
  with the password it was staged with. Its `README.txt` describes the formats so it could even be read without bdxl.
- **Key rotation** starts a new master key for everything new and keeps the old ones in a *key ring*, so older discs
  stay readable ([Security](Guide-Security.md#rotating-the-master-key)).

## The agent

`bdxl unlock` hands the master key to a small background program, the *agent*, so commands do not ask for the
password every time. It keeps keys in memory only, for your Windows user only, and locks them after a while of
not being used, at a maximum time, on Win+L and on sleep.

## Compression

Optional, per location: files can be compressed (zstd) inside their encryption. `scan` counts the exact compressed
size, so discs are still filled exactly. See [Compression](Guide-Compression.md).

## Split files

A file larger than any blank disc can be cut into parts, one per disc ([Large Files](Guide-Large-Files.md)).
It counts as backed up only when all its parts are on discs.

## Integrity everywhere

- Every copy written to a staging folder is checked against the source's hash.
- `verify` re-reads a burned disc and compares every file. Encrypted files are compared by the hash of the stored
  file, so nothing is decrypted; `--deep` also decrypts and compares the content.
- `restore` checks every restored file against its hash.
- Catalog backups carry Reed-Solomon repair data and can be repaired without the password.
