# Concepts

[Home](Home.md) › Concepts

## Catalog

The catalog is the folder (`--catalog`, default `.\catalog`) where discloak keeps everything it knows:

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
restored files go to `<destination>\<alias>\<path>`. `discloak alias` lists them; `alias rename` renames one.

## Pending files and deduplication

A file is **pending** when it is on its source, not ignored, hashed, and fewer *live* discs (burned or staged, not
lost or destroyed) hold a complete copy of its content than its location wants: one by default, more with
[`copies`](Guide-Copies.md), each copy on a different disc. Content is recognised by hash, so:

- identical files (in one or several locations) are stored once; the others are linked to that copy;
- a file whose content is already on a disc is not stored again, even under another name;
- a changed file is pending again; the older version stays recorded on its disc.

`discloak status` and `discloak plan` show what is pending.

## Discs, labels and states

Each staged disc gets a label `<prefix><counter>`, e.g. `DISC-0007`. The counter only ever goes up. States:

| State | Meaning |
|---|---|
| staged | its folder was written, it is not burned yet (`burned` or `discard` next) |
| burned | recorded as burned; its copies count as backups |
| lost | missing; its copies do not count, files still on the source are pending again; `found` undoes it |
| destroyed | gone for good (final) |

A burned disc that is not finalized can take more data in further *sessions* ([Appending](Guide-Appending.md)).

## Blank discs and reserve

`discloak blanks` is your stock of blank discs: size, reserve, price. Staging uses one up; `discard` gives it back.

<a id="disc-space"></a>**Space on a disc**: the files are laid end to end in one stream of data, cut into **packs** of
64 MiB ([Files and Formats](Files-and-Formats.md#a-staged--burned-disc)), so a file takes exactly its stored size
(compressed, if set) - a small file needs no sectors or file system entries of its own. Each file also has a line in
the disc's encrypted manifest (its path and hashes, roughly 300-500 bytes). The packs themselves cost almost nothing
(4 KB each, for their sectors, entries and hash line); a disc's room is its usable space less one pack (the last pack
is filled up with zeros) and one more 64 MiB for the manifest (padded the same way). `plan`, `stage` and `--append`
count all of it.

<a id="reserve"></a>The **reserve** is space kept free on each disc. It has to hold the file system's own fixed overhead
(volume descriptors, the metadata partition), the disc's folders and its own small files (`disc.json`, `discloak.key`,
`README.txt`; the manifest is counted with the files) and, with `--with-catalog`, the catalog backup: the catalog's
`.enc` files (compressed, roughly 100-150 bytes per catalogued file) plus 25% repair data. If those come out larger
than the reserve, `stage` warns that the folder will not fit. Raise it with `discloak blanks reserve 500MB`.

The reserve can also keep room to **burn a damaged pack again**: a new disc that is left open until it has passed
`verify` can take a pack that failed as a small second session ([Backing Up › If verify finds a damaged
pack](Guide-Backing-Up.md#if-verify-finds-a-damaged-pack)). That takes 64 MiB per pack plus what a session costs on
the medium (a few MB, depending on the burning program): a reserve 200 MB larger keeps room for about two packs.

## Encryption, the password and the master key

- A random **master key** encrypts the catalog and everything on discs (AES-256-GCM). On a disc, the files are in
  packs of equal size (`data/000/000001.pack` …), so nothing shows their names, sizes or number.
- The **password** only unlocks the master key, which is stored wrapped in `discloak.key` (Argon2id key derivation).
  Changing the password rewrites `discloak.key` and nothing else.
- The optional **recovery key** is a second way to unlock it, for a lost password.
- Every encrypted disc carries the `discloak.key` of its day. A disc can be read with the catalog, or on its own
  with the password it was staged with. Its `README.txt` describes the formats so it could even be read without discloak.
- **Key rotation** starts a new master key for everything new and keeps the old ones in a *key ring*, so older discs
  stay readable ([Security](Guide-Security.md#rotating-the-master-key)).

## The agent

`discloak unlock` hands the master key to a small background program, the *agent*, so commands do not ask for the
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
