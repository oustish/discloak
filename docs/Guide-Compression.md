# Compression

[Home](Home.md) › Compression

Files can be compressed (zstd) before they go into a disc's encrypted packs. It is set **per location** (alias),
with a default for all. It is off unless you turn it on.

## How it works

- Every 1 MiB piece of a file becomes one zstd frame: together a standard zstd stream (`zstd -d` reads it).
  Incompressible data grows by about 30 bytes per MiB - `auto` does not even try the usual compressed formats.
- `scan` also counts the **exact** size each file will have on the disc, in the same read as hashing, so `plan`
  and `stage` still fill discs exactly. A file whose size was not counted for today's setting is stored
  uncompressed.
- The setting only affects new copies. Copies already on discs stay as they are, and restoring never needs any
  setting.

Modes:

| Mode | Compresses |
|---|---|
| `off` | nothing (default) |
| `auto` | every file except those whose extension says they are compressed already (`.jpg .png .heic .mp4 .mkv .mp3 .flac .zip .7z .docx .xlsx` … ) |
| `on` | every file |

Level: zstd 1–19, default 3. Higher levels pack a little tighter and are much slower, for `scan` and `stage` alike.

## Turn it on for a location

1. Set it:
   ```
   bdxl compression set Docs auto
   ```
2. Count the compressed sizes (reads the affected files once more):
   ```
   bdxl scan
   ```
   Until this has run, `plan` and `stage` count those files uncompressed, so nothing ever overfills.
3. See the effect:
   ```
   bdxl compression list
   ```
   ```
   Default: off
     Docs                     auto, level 3
         1,204 of 1,250 file(s) are compressed; counted: 3,412,000,000 -> 1,250,000,000 bytes (37%)
     Photos                   off  (default)
   ```

## Other settings

```
bdxl compression set --default auto            # every location without its own setting
bdxl compression set Docs on --level 9         # another mode or level for one location
bdxl compression clear Docs                    # back to the default
```

A changed level means the sizes are counted again on the next `scan`.

## Good to know

- Photos, video, music and archives hardly compress. `auto` skips them without spending time on them.
- Text, documents (non-Office), code, logs, databases, uncompressed images (BMP/TIFF) and many RAW formats compress well.
- The level used is recorded on every copy (and in the disc's manifest), so `restage` rebuilds a disc at the same
  sizes even after you change the setting.
- Parts of split files are never compressed.
- Renaming a location (`alias rename`) keeps its compression setting.
