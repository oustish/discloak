# Backing Up

[Home](Home.md) › Backing Up

The regular cycle: **scan → plan → stage → burn → verify → burned**. Run it whenever enough new data has piled up.

## The regular cycle

1. **Unlock** (optional, saves typing the password for every command):
   ```
   discloak unlock
   ```
2. **Rescan everything** you have scanned before (unchanged files are not read again):
   ```
   discloak scan
   ```
   Or one location: `discloak scan D:\Photos`. A location on a drive that is not connected is skipped; nothing in it
   is marked as missing.
3. **Look at the plan**:
   ```
   discloak status
   discloak plan
   ```
   `plan` lists the discs the pending data would fill. Exit code 2 means not everything fits on the blank discs you
   registered. Add more with `discloak blanks add`, or just stage what fits.
4. **Stage** the next disc:
   ```
   discloak stage E:\Staging --with-catalog
   ```
   - `--with-catalog` puts a catalog backup on the disc (`_catalog\`). Recommended: with it, any disc can bring the
     whole catalog back ([Disaster Recovery](Guide-Disaster-Recovery.md)).
   - `--max-discs 3` stages up to three discs at once (folders `DISC-0001`, `DISC-0002`, `DISC-0003`).
   - `--min-fill 0.9` only stages discs that would be at least 90% full, so half-empty discs wait for more data.
5. **Burn** the contents of `E:\Staging\DISC-0001` with your burning program, with a file system the disc supports
   ([which one](Troubleshooting.md#file-system)). **Do not finalize (close)
   the disc yet**: while it is open, a pack that fails `verify` can be burned again on its own, as a small second
   session ([below](#if-verify-finds-a-damaged-pack)). Finalize it once it has passed `verify` - or leave it open if
   you might add more to it later ([Appending](Guide-Appending.md)). Write the label on the disc.
6. **Verify** the burned disc (here in drive `G:`):
   ```
   discloak verify DISC-0001 G:\ --deep
   ```
   Every pack is read and its hash compared with the catalog. `--deep` also decrypts every pack and checks every
   file against its hash, in the same single read (proves the content can be restored; without it, no password is
   needed). The disc's other files are checked first: its manifest, `packs.sha256`, `disc.json`, `discloak.key` and
   the catalog backup in `_catalog\` (against its repair data). A status line shows the progress, speed and time left.
7. **Record** it:
   ```
   discloak burned DISC-0001                      (or --date 2026-10-29 for another day)
   ```
   Or let step 6 do it: `discloak verify DISC-0001 G:\ --deep --mark-burned` marks the disc as burned if, and only
   if, everything checked out.
8. Delete `E:\Staging\DISC-0001` - only now, after `verify`: it is what a damaged pack is burned again from.
9. `discloak lock` when you are done (or let it lock by itself).

## If something goes wrong while staging

| Situation | What to do |
|---|---|
| Ctrl+C during `stage` | The file being copied is left out and the disc is finished with what was copied: a complete, smaller folder. Burn it as it is, or `discloak discard DISC-0001` and stage again. |
| "changed since last scan - not staged" | The file changed after the scan. Run `discloak scan` again; it is pending again. The space it took in its pack stays unused on this disc. |
| "WARNING: … folder is … more than the disc holds" | The disc's own files outgrew the reserve. `discloak discard DISC-0001`, `discloak blanks reserve 500MB`, stage again. |
| You decide not to burn a staged disc | `discloak discard DISC-0001`: its files are pending again and the blank disc goes back to stock. The folder is not deleted; delete it yourself. |
| The burned disc fails `verify` | Do not run `burned`. See [If verify finds a damaged pack](#if-verify-finds-a-damaged-pack): burn the damaged packs again as a new session, or the same folder onto a new disc. If the folder is gone: `discloak discard` and stage again. |

## If verify finds a damaged pack

`verify` names each damaged pack (`DAMAGED  pack 312 …`) and the files with data in it. A read that fails or comes
back wrong is tried again first (twice, from the disc itself: `--retries`); one that comes right on a retry is shown
as `RE-READ` and is not damage - but if they are many, suspect the disc, the drive or its cable (try another drive,
port or cable, and `--no-cache`).

<a id="damaged-copies"></a>**What verify records.** The copies it finds damaged are marked in the catalog: without
`--deep`, every file with data in a damaged pack; with `--deep`, only the files that do not come back intact (the
damage may lie in another file's part of the pack). A marked copy:

- no longer counts as a backup: a file still on its source is **pending again**, so `plan` and `stage` make a new
  copy of it (on another disc), and `status`, `copies` and `find` count it as missing;
- stays in the catalog: `find` shows it (`[DAMAGED, found by verify on …]`), `status` warns about it, and `restore`
  still tries it, after every other copy (the damage may cost only a part of it);
- loses the mark when a later `verify` reads it back intact as a whole (also `verify --packs` of its packs), with a
  `WARNING`: a copy that fails and then passes points to unreliable reads - the drive, its cable or connection - or to
  a disc that is wearing, unless its packs were burned again in between. `restage` of the disc removes the marks too.

### A new disc, not marked as burned yet

Keep the staging folder, and do not finalize the disc. If the disc has room left:

1. Check the folder's copy of the pack (it is the one the catalog has):
   ```
   discloak verify DISC-0001 E:\Staging\DISC-0001 --packs 312
   ```
2. With your burning program, add a session to the disc that **continues its file system** ("multisession",
   "import previous session") and holds only the damaged pack file(s), at the same place: `data\000\000312.pack`
   (`verify` prints the paths). The new copy takes the old one's place in the file system; the damaged spot stays
   on the disc, unused.
3. Check it, then the whole disc:
   ```
   discloak verify DISC-0001 G:\ --packs 312 --deep
   discloak verify DISC-0001 G:\ --mark-burned
   ```

This needs 64 MiB of free space per pack, plus what a session costs on the medium (it depends on the burning
program; a few MB). To always keep room for it, add it to the reserve ([Concepts › Reserve](Concepts.md#reserve)).
A later `stage --append` to this disc should be given `--mount`, so that it sees the space the repair session took.
If many packs fail, or the same ones fail again, the disc (or the burn) is bad: burn the folder onto a new disc.

### An older disc (marked as burned)

The damaged copies are marked ([above](#damaged-copies)), so the next `stage` backs up again those files that are
still on their source; nothing else needs doing for them. Beyond that:

- Files that have a copy on another disc are safe: `restore` takes them from a disc where they read back intact.
- Files that are gone from their source and have no other copy can only come back from this disc: `restore` them
  now (a damaged spot costs only the files with data in it), and rebuild the disc.
- **Rebuild the disc**: `discloak restage DISC-0001 E:\Staging --from G:\` writes it again onto a new blank, with
  the intact packs copied from the old disc and the damaged ones rebuilt from the source files
  ([Disaster Recovery](Guide-Disaster-Recovery.md#a-disc-is-damaged-or-lost)).
- **Give the disc up**: `discloak lost` or `destroyed` takes **all** its copies out of the count (not only the
  damaged ones); files still on their source become pending, and the next `stage` backs them up again.
- A disc that is still open, with its staging folder still there, can also get its damaged packs burned again as
  above (the packs from the folder; a rebuilt pack would be encrypted anew and not match the catalog).

## Checking how things stand

```
discloak status          # sources, pending data, blank discs, discs; warns about files that exist nowhere any more
discloak discs           # every disc, its state, sessions, free space
discloak find report.pdf # where a file is backed up
discloak cost report     # what the discs cost, per disc / GB / TB
```

## Checking old discs now and then

Discs age. Re-read them from time to time:

```
discloak verify DISC-0003 G:\
```

A disc that fails: see [Disaster Recovery › A disc is damaged](Guide-Disaster-Recovery.md#a-disc-is-damaged-or-lost).
The copies it finds damaged stop counting as backups, so the next `stage` makes new copies of those files
([What verify records](#damaged-copies)). A pack that failed can be checked again on its own:
`discloak verify DISC-0003 G:\ --packs 312 --deep` - if it reads back intact now, the marks go, with a warning.
