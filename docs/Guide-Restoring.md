# Restoring Files

[Home](Home.md) › Restoring Files

`restore` finds files in the catalog, tells you which discs it needs, copies them from the discs you put in,
decrypts them and checks each one against its hash.

## Find what you need

```
bdxl find holiday            # any part of a path
bdxl find 3FA9C1             # or the start of a SHA-256
bdxl find "*"                # everything
```

The output shows each match, every disc holding it, when it was burned, and whether it is the current version.

## Restore files (one disc in one drive)

1. Unlock (or have the password ready): `bdxl unlock`
2. Put the disc in, e.g. drive `G:`, and run:
   ```
   bdxl restore D:\Recovered "holiday" --mount G:\
   ```
3. Files go to `D:\Recovered\<alias>\<path>`, e.g. `D:\Recovered\Photos\2019\holiday\IMG_0001.jpg`.
4. If the files are spread over several discs, the end of the output says which ones are still needed:
   ```
   Insert / mount these discs and run the same command again with --mount:
     BDXL-0004  (12 file(s))
   ```
   Swap the disc and run the same command again. Files already restored show as `EXISTS` and are skipped.

## Several drives at once

```
bdxl restore D:\Recovered "*" --mount "F:\;G:\"
```

Discs are recognised by their `disc.json`, whatever drive they are in.

## Useful options

| Option | Effect |
|---|---|
| `--source Photos` | only files of this location (alias) |
| `--flat` | all files straight into the destination folder, without the alias/path folders |
| `--overwrite` | replace files that already exist in the destination |
| `--disc BDXL-0004` | the disc in `--mount` has no readable `disc.json`: tell bdxl which disc it is |

## Restore everything

```
bdxl restore D:\Recovered "*" --mount G:\
```

Then repeat with each disc it asks for. The query `*` matches all files. Use `--source <alias>` for one location.

## Which version is restored?

The current version (the content the source had at the last scan) if any disc has it. Otherwise the newest older
version found on a live disc. Files that are on no surviving disc are listed at the end ("no surviving copy").

## Split files

A file stored in parts on several discs is collected disc by disc. Each verified part waits next to the target as
`<file>.bdxl-part.001` …; when the last one is there, the parts are joined, the whole file is checked, and the parts
are removed. Put the discs in in any order and run the same command each time.

## Interrupting

Ctrl+C stops after the current file (a half-written file is removed) and says how many files were not looked at.
Run the same command again to continue.

## Reading a disc without the catalog

If the catalog is gone, start a new one from the disc's own key and read the disc into it. See
[Disaster Recovery › The catalog is lost](Guide-Disaster-Recovery.md#the-catalog-is-lost).

## Checking a disc without restoring

```
bdxl verify BDXL-0004 G:\          # stored files intact?
bdxl verify BDXL-0004 G:\ --deep   # and they decrypt to the right content?
```
