# Backing Up

[Home](Home.md) › Backing Up

The regular cycle: **scan → plan → stage → burn → verify → burned**. Run it whenever enough new data has piled up.

## The regular cycle

1. **Unlock** (optional, saves typing the password for every command):
   ```
   bdxl unlock
   ```
2. **Rescan everything** you have scanned before (unchanged files are not read again):
   ```
   bdxl scan
   ```
   Or one location: `bdxl scan D:\Photos`. A location on a drive that is not connected is skipped; nothing in it
   is marked as missing.
3. **Look at the plan**:
   ```
   bdxl status
   bdxl plan
   ```
   `plan` lists the discs the pending data would fill. Exit code 2 means not everything fits on the blank discs you
   registered. Add more with `bdxl blanks add`, or just stage what fits.
4. **Stage** the next disc:
   ```
   bdxl stage E:\Staging --with-catalog
   ```
   - `--with-catalog` puts a catalog backup on the disc (`_catalog\`). Recommended: with it, any disc can bring the
     whole catalog back ([Disaster Recovery](Guide-Disaster-Recovery.md)).
   - `--max-discs 3` stages up to three discs at once (folders `BDXL-0001`, `BDXL-0002`, `BDXL-0003`).
   - `--min-fill 0.9` only stages discs that would be at least 90% full, so half-empty discs wait for more data.
5. **Burn** the contents of `E:\Staging\BDXL-0001` with your burning program (UDF 2.50). If you might add more to
   this disc later ([Appending](Guide-Appending.md)), leave it open (do not finalize). Write the label on the disc.
6. **Verify** the burned disc (here in drive `G:`):
   ```
   bdxl verify BDXL-0001 G:\
   ```
   Every file is read and compared. `--deep` also decrypts each file and compares the original content (slower,
   proves the content can be restored).
7. **Record** it:
   ```
   bdxl burned BDXL-0001                      (or --date 2026-10-29 for another day)
   ```
8. Delete `E:\Staging\BDXL-0001`.
9. `bdxl lock` when you are done (or let it lock by itself).

## If something goes wrong while staging

| Situation | What to do |
|---|---|
| Ctrl+C during `stage` | The file being copied is removed and the disc is finished with what was copied: a complete, smaller folder. Burn it as it is, or `bdxl discard BDXL-0001` and stage again. |
| "changed since last scan - not staged" | The file changed after the scan. Run `bdxl scan` again; it is pending again. |
| "WARNING: … folder is … more than the disc holds" | The disc's own files outgrew the reserve. `bdxl discard BDXL-0001`, `bdxl blanks reserve 500MB`, stage again. |
| You decide not to burn a staged disc | `bdxl discard BDXL-0001`: its files are pending again and the blank disc goes back to stock. The folder is not deleted; delete it yourself. |
| The burned disc fails `verify` | Do not run `burned`. Burn the same folder onto a new disc and verify again. If the folder is gone: `bdxl discard` and stage again. |

## Checking how things stand

```
bdxl status          # sources, pending data, blank discs, discs; warns about files that exist nowhere any more
bdxl discs           # every disc, its state, sessions, free space
bdxl find report.pdf # where a file is backed up
bdxl cost report     # what the discs cost, per disc / GB / TB
```

## Checking old discs now and then

Discs age. Re-read them from time to time:

```
bdxl verify BDXL-0003 G:\
```

A disc that fails: see [Disaster Recovery › A disc is damaged](Guide-Disaster-Recovery.md#a-disc-is-damaged-or-lost).
