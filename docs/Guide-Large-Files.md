# Large Files (split across discs)

[Home](Home.md) › Large Files

A file larger than every blank disc (a disk image, a long video) does not fit anywhere. `plan` reports it as too
big. With `--split`, it is cut into parts, one per disc, each encrypted on its own.

## Steps

1. See it:
   ```
   bdxl plan                 # lists the file as too big
   bdxl plan --split         # shows the parts on the discs
   ```
2. Stage with `--split`, as many discs as the parts need:
   ```
   bdxl stage E:\Staging --split --max-discs 3 --with-catalog
   ```
3. Burn, verify and record each disc as usual.

## How it behaves

- Parts are as large as the largest blank disc allows (minus its reserve and the encryption overhead).
- The file counts as backed up only when **every** part is on a live disc. `plan` says how many split files still
  have parts to store.
- Once a file has parts on discs, its remaining parts are staged even without `--split`, with the same layout.
- Parts are never compressed (their layout is fixed in plain bytes).
- The source must stay exactly as scanned (same size and time) while its parts are staged; otherwise run `scan` again.
- If a disc with one part is lost, only that part is pending again.

## Restoring

```
bdxl restore D:\Recovered "image.vhdx" --mount G:\
```

Put the discs in one after another and run the same command each time. Verified parts wait next to the target as
`image.vhdx.bdxl-part.001` …; when all are there, they are joined and the whole file is checked against its hash.
