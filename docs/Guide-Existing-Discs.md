# Discs You Already Have

[Home](Home.md) › Discs You Already Have

`scandisc` records a disc that is already burned, so its files count as backed up and can be found and restored.
It works for:
- discs burned without bdxl (plain files);
- bdxl discs when the catalog was lost or never knew them (encrypted discs of this catalog's key);
- a folder laid out like a disc.

## Steps for an older disc burned without bdxl

1. Scan your source folders first (`bdxl scan …`), so the disc's files can be matched to them.
2. Put the disc in (e.g. `G:`) and read it:
   ```
   bdxl scandisc G:\ --label OLD-2019-01 --method hash --date 2019-01-15 --capacity 25GB --cost 3.50
   ```
   - `--label`: the name to give it (otherwise from its `disc.json` or the drive's volume label).
   - `--method hash` reads and hashes every file: slow, but exact.
   - `--date`, `--capacity`, `--cost`: optional facts about the disc.
   - `--dry-run` shows what would be recorded without changing anything.
3. Read the report: how many files matched known files (same version, an older version, same content under another
   path) and how many are new entries ("not seen in any source scan").
4. Optionally put a `disc.json` on it so it identifies itself later (only if it can still be written to):
   ```
   bdxl discjson OLD-2019-01 G:\
   ```
   or right away with `scandisc … --write-disc-json`.

Files that match pending files count as backed up from now on. Plain discs can be verified, restored and rebuilt
(`restage`) as they are, but bdxl adds nothing to them (no `--append`): everything new is encrypted.

## Methods

| Method | What it does | Use when |
|---|---|---|
| `manifest` | trusts the disc's manifest (fast); copies are marked *unverified* until `verify` | bdxl discs (the only way to link the hidden names of an encrypted disc) |
| `hash` | reads and hashes every file | discs burned without bdxl; to be sure |
| `quick` | matches path + size + time only, reads nothing | huge plain discs you trust; run `verify` later |
| `auto` (default) | `manifest` if the disc has one, otherwise `hash` | |

## A disc that is already in the catalog

`scandisc` refuses a label the catalog already has. To merge what is on the disc into its record:
```
bdxl scandisc G:\ --update
```
To just check it: `bdxl verify <label> G:\`.

## An encrypted disc of another catalog

A disc made with another catalog's key cannot be read by this catalog (`scandisc` says so). Open it with its own
catalog, or start a catalog from the disc's key: [Disaster Recovery › The catalog is lost](Guide-Disaster-Recovery.md#the-catalog-is-lost).
