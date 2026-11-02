# Reading a Disc Back into the Catalog

[Home](Home.md) › Reading a Disc Back into the Catalog

`scandisc` records a disc made by bdxl in the catalog, from the disc's own encrypted manifests. You need it when the
catalog does not know the disc (any more):
- the catalog was lost and is rebuilt from the discs ([Disaster Recovery](Guide-Disaster-Recovery.md#the-catalog-is-lost));
- the catalog was rolled back or restored from an older backup, and discs were staged after that version;
- a folder laid out like a disc (a copy of one).

Discs written with other tools (plain files) cannot be recorded: bdxl reads only its own discs.

## Steps

1. If your sources still exist, scan them first (`bdxl scan …`), so the disc's files are matched to them.
2. Put the disc in (e.g. `G:`) and read it:
   ```
   bdxl scandisc G:\ --capacity 100GB --cost 45
   ```
   - The label, date and key come from the disc's `disc.json`; `--date` sets another burn date.
   - `--capacity` (the disc's size) lets `stage --append` add a session to it later; `--cost` records its price.
   - `--dry-run` shows what would be recorded without changing anything.
3. Read the report: how many files matched known files (same version, another version of the same path, same content
   under another path) and how many are new entries ("not seen in any source scan").
4. Check it: `bdxl verify <label> G:\`. Until then its copies count as *unverified*.

Only the manifests are decrypted: reading a disc back takes seconds, whatever its size. Its packs' hashes come from
its `packs.sha256`; `verify` compares the packs with them, `verify --deep` also decrypts every file.

## A disc that is already in the catalog

`scandisc` refuses a label the catalog already has. To merge what is on the disc into its record:
```
bdxl scandisc G:\ --update
```
To just check it: `bdxl verify <label> G:\`.

## A disc of another catalog

A disc made with another catalog's key cannot be read by this catalog (`scandisc` says so). Open it with its own
catalog, or start a catalog from the disc's key: [Disaster Recovery › The catalog is lost](Guide-Disaster-Recovery.md#the-catalog-is-lost).
