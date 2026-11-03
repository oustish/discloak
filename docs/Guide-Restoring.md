# Restoring Files

[Home](Home.md) › Restoring Files

`restore` finds files in the catalog, tells you which discs it needs, reads them from the discs you put in (only the
parts of the packs each file needs), decrypts them and checks each one against its hash. A damaged spot on a disc
costs only the files with data in it: they are taken from another mounted disc with a copy if there is one, and
everything else is restored.

## Find what you need

```
discloak find holiday            # any part of a path
discloak find 3FA9C1             # or the start of a SHA-256
discloak find "*"                # everything
```

The output shows each match, every disc holding it, when it was burned, and whether it is the current version.

## Restore files (one disc in one drive)

1. Unlock (or have the password ready): `discloak unlock`
2. Put the disc in, e.g. drive `G:`, and run:
   ```
   discloak restore D:\Recovered "holiday" --mount G:\
   ```
3. Files go to `D:\Recovered\<alias>\<path>`, e.g. `D:\Recovered\Photos\2019\holiday\IMG_0001.jpg`.
4. If the files are spread over several discs, the end of the output says which ones are still needed:
   ```
   Insert / mount these discs and run the same command again with --mount:
     DISC-0004  (12 file(s))
   ```
   Swap the disc and run the same command again. Files already restored show as `EXISTS` and are skipped.

## Several drives at once

```
discloak restore D:\Recovered "*" --mount "F:\;G:\"
```

Discs are recognised by their `disc.json`, whatever drive they are in.

## Useful options

| Option | Effect |
|---|---|
| `--source Photos` | only files of this location (alias) |
| `--flat` | all files straight into the destination folder, without the alias/path folders |
| `--overwrite` | replace files that already exist in the destination |
| `--disc DISC-0004` | the disc in `--mount` has no readable `disc.json`: tell discloak which disc it is |

## Restore everything

```
discloak restore D:\Recovered "*" --mount G:\
```

Then repeat with each disc it asks for. The query `*` matches all files. Use `--source <alias>` for one location.

## Which version is restored?

The current version (the content the source had at the last scan) if any disc has it. Otherwise the newest older
version found on a live disc. Files that are on no surviving disc are listed at the end ("no surviving copy").

## Split files

A file stored in parts on several discs is collected disc by disc. Each verified part waits next to the target as
`<file>.discloak-part.001` …; when the last one is there, the parts are joined, the whole file is checked, and the parts
are removed. Put the discs in in any order and run the same command each time.

## Interrupting

Ctrl+C stops after the current file (a half-written file is removed) and says how many files were not looked at.
Run the same command again to continue.

## Reading a disc without the catalog

If the catalog is gone, start a new one from the disc's own key and read the disc into it. See
[Disaster Recovery › The catalog is lost](Guide-Disaster-Recovery.md#the-catalog-is-lost).

## Checking a disc without restoring

```
discloak verify DISC-0004 G:\          # packs intact? (no password needed for this part)
discloak verify DISC-0004 G:\ --deep   # and every file decrypts to the right content?
```
