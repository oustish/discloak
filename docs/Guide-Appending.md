# Appending to a Disc (multisession)

[Home](Home.md) › Appending to a Disc

A burned disc that was **not finalized** and is not full can take more data later, as a new *session*. bdxl stages
only the files that fit, with a manifest of **their own** (`manifest.s2.jsonl.enc`, `.s3` …: only the new files),
`disc.json` and (optionally) `_catalog`. Burned as the next session, `disc.json` and `_catalog` replace the old ones;
the earlier manifests stay, and bdxl reads all of them in session order. (Discs appended by older versions of bdxl,
whose later manifest repeated the whole list, read the same way.)

Requirements:
- the disc was burned without finalizing (closing) it;
- your burning program can add a session that **continues the existing file system** ("multisession",
  "import previous session"). Otherwise the earlier files disappear from view;
- the disc is encrypted with this catalog's key (every disc bdxl stages is; plain discs from before cannot take one).

## Steps

1. See how much would go in:
   ```
   bdxl plan --append BDXL-0003
   ```
   Free space = disc size − earlier sessions − the disc's reserve (every session needs it again).
   `bdxl discs` also shows each disc's free space.
2. Put the disc in the drive (recommended, e.g. `G:`) and stage the session:
   ```
   bdxl stage E:\Staging --append BDXL-0003 --mount G:\ --with-catalog
   ```
   - `--mount` checks that the disc in the drive is `BDXL-0003` (and has this catalog's key) and uses the free space
     the drive reports if that is smaller. Many burning programs leave an open disc read-only for Windows, which then
     reports 0 free; bdxl then uses the catalog's figure.
   - The session folder is `E:\Staging\BDXL-0003.s2` (`.s3`, `.s4` … later).
3. Burn the folder onto `BDXL-0003` as its next session, continuing the file system.
4. Verify the whole disc and record the session:
   ```
   bdxl verify BDXL-0003 G:\
   bdxl burned BDXL-0003
   ```
   `burned` dates only the copies of this session.

## Notes

- A newer version of a file that is already on the disc gets a new hidden name; both versions stay restorable.
- Only one unburned session at a time. To take a staged session back: `bdxl discard BDXL-0003` (the burned part of
  the disc stays recorded).
- While a session is staged but not burned, `lost`, `destroyed` and `restage` of that disc are refused.
- `restage` of a disc with several sessions rebuilds it as one folder (to burn onto a new blank disc).
- Disc size unknown (a disc brought in with `scandisc` without `--capacity`)? Use `--mount`, so the drive's free
  space is used.
