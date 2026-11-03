# Appending to a Disc (multisession)

[Home](Home.md) › Appending to a Disc

A burned disc that was **not finalized** and is not full can take more data later, as a new *session*. discloak stages
only the files that fit, in packs that continue the disc's numbering, with a manifest of **their own**
(`manifest.s2.enc`, `.s3` …: only the new files), `packs.sha256`, `disc.json` and (optionally) `_catalog`. Burned as
the next session, `packs.sha256`, `disc.json` and `_catalog` replace the old ones (`packs.sha256` lists every pack of
the disc); the earlier packs and manifests stay, and discloak reads all the manifests in session order.

Requirements:
- the disc was burned without finalizing (closing) it;
- your burning program can add a session that **continues the existing file system** ("multisession",
  "import previous session"). Otherwise the earlier files disappear from view;
- the disc is encrypted with a key of this catalog (the session uses the disc's own key, even after a key rotation).

## Steps

1. See how much would go in:
   ```
   discloak plan --append DISC-0003
   ```
   Free space = disc size − earlier sessions − the disc's reserve (every session needs it again).
   `discloak discs` also shows each disc's free space.
2. Put the disc in the drive (recommended, e.g. `G:`) and stage the session:
   ```
   discloak stage E:\Staging --append DISC-0003 --mount G:\ --with-catalog
   ```
   - `--mount` checks that the disc in the drive is `DISC-0003` (and has this catalog's key) and uses the free space
     the drive reports if that is smaller. Many burning programs leave an open disc read-only for Windows, which then
     reports 0 free; discloak then uses the catalog's figure.
   - The session folder is `E:\Staging\DISC-0003.s2` (`.s3`, `.s4` … later).
3. Burn the folder onto `DISC-0003` as its next session, continuing the file system.
4. Verify the whole disc and record the session:
   ```
   discloak verify DISC-0003 G:\
   discloak burned DISC-0003
   ```
   `burned` dates only the copies of this session.

## Notes

- A newer version of a file that is already on the disc is stored again in the new packs; both versions stay restorable.
- Each session costs up to one pack (its last pack is filled up) and one 64 MiB manifest, besides the reserve.
- Only one unburned session at a time. To take a staged session back: `discloak discard DISC-0003` (the burned part of
  the disc stays recorded).
- While a session is staged but not burned, `lost`, `destroyed` and `restage` of that disc are refused.
- `restage` of a disc with several sessions rebuilds it as one folder (to burn onto a new blank disc).
- Disc size unknown (a disc brought in with `scandisc` without `--capacity`)? Use `--mount`, so the drive's free
  space is used.
