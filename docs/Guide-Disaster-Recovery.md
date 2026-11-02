# Disaster Recovery

[Home](Home.md) › Disaster Recovery

| What happened | Go to |
|---|---|
| A command says the catalog failed its integrity check | [The catalog is damaged](#the-catalog-is-damaged-or-fails-its-check) |
| The catalog folder is gone (disk died, deleted) | [The catalog is lost](#the-catalog-is-lost) |
| You forgot the password | [Forgotten password](#forgotten-password) |
| A disc fails `verify`, cannot be read, or is lost | [A disc is damaged or lost](#a-disc-is-damaged-or-lost) |
| A catalog backup is damaged | [A catalog backup is damaged](#a-catalog-backup-is-damaged) |

---

## The catalog is damaged or fails its check

Every command checks the catalog first and refuses to work on a damaged one (exit code 4).

1. See what is wrong:
   ```
   bdxl catalog check
   ```
   It lists each problem: a file damaged, altered, missing, written with another key, or a hash list that fails its
   HMAC (files from different saves mixed together).
2. Pick a way out:
   - **Take it from the mirror**, if you have a [catalog mirror](Guide-Mirror.md) (the message says whether it
     checks out): the most recent state, nothing lost:
     ```
     bdxl catalog mirror restore
     ```
   - **Go back to an earlier version** (one is kept before every command that changed the catalog):
     ```
     bdxl catalog history
     bdxl catalog rollback 20261030-091500-stage
     ```
     Then re-run what happened after that version, e.g. `bdxl scan`. Discs staged after it can be brought back with
     `bdxl scandisc <disc> --update` (or `scandisc` if the disc is unknown to that version).
   - **Restore a catalog backup** (from a disc's `_catalog` folder or a `catalog backup create` folder):
     ```
     bdxl catalog backup verify G:\_catalog --repair      (only if it reports damage)
     bdxl catalog backup restore G:\_catalog --force
     ```
     The current catalog is kept in history first, and your current password stays.
   - **Accept the current state**: only if every file still decrypts and you believe the content is right (for
     example after a hash file got out of step):
     ```
     bdxl catalog accept
     ```

## The catalog is lost

You need the password (or the recovery key) and either a catalog backup or the discs themselves.

### With a catalog backup (best)

The newest backup is usually on your newest disc (`stage --with-catalog`) or in a `catalog backup create` folder.

1. Check the backup; repair it if needed (no password needed):
   ```
   bdxl catalog backup verify G:\_catalog
   bdxl catalog backup verify G:\_catalog --repair
   ```
2. Restore it into a new, empty catalog folder:
   ```
   bdxl catalog backup restore G:\_catalog --catalog D:\bdxl-catalog
   ```
   It asks for the password that was valid when the backup was made (or use `--recovery` for the recovery key).
3. Bring it up to date:
   - discs staged after that backup: `bdxl scandisc <disc>` for each (their manifests list their files);
   - your sources: `bdxl scan`.
4. Optional: `bdxl password change` if you want today's password back.

### Without a catalog backup: from the discs

Every encrypted disc carries the catalog's key file (`bdxl.key`) of the day it was staged.

1. Start a new catalog with the key of your **newest** disc. It knows every older key too, under the newest
   password:
   ```
   bdxl init --key-from G:\ --catalog D:\bdxl-catalog
   ```
   It asks for the password that was valid when that disc was staged (or use `--recovery`).
2. Read every disc into it:
   ```
   bdxl scandisc G:\ --catalog D:\bdxl-catalog
   ```
   Repeat for each disc. Files are recorded under their real paths from the encrypted manifests.
3. Rescan your sources, if any are left (`bdxl scan <folder> --name <alias>`, using the same aliases), and
   re-register your blank discs (`bdxl blanks add …`).

> Only have an old disc and its old password? `init --key-from` on that disc works too, but that catalog then has
> only the keys up to that disc's day: discs staged after a later key rotation stay closed to it.

## Forgotten password

- **First, find out which password you have:** `bdxl password check` tries one password without changing anything
  and says when the catalog's password was set, so you can look for the entry (or entry history) of that date.
  `bdxl password check <disc>` does the same for a disc. See [Checking a password](Guide-Security.md#checking-a-password).
- **With the recovery key:**
  ```
  bdxl password change --recovery
  ```
  Type (or set `BDXL_RECOVERY_KEY` to) the recovery key, then a new password (pasted twice from your password manager, then confirmed).
- **Without the recovery key, but you remember an older password:** discs staged while that password was valid still
  open with it. `bdxl init --key-from <such a disc> --catalog D:\recovered` starts a catalog that reads them.
- **Neither:** the data cannot be decrypted. That is the point of the encryption.

## A disc is damaged or lost

With [several copies](Guide-Copies.md) nothing is lost yet: `restore` takes the files from another disc, and
`lost` / `destroyed` make the next `stage` plan the missing copy again. Without them:

1. Check it:
   ```
   bdxl verify BDXL-0005 G:\
   ```
   `DAMAGED pack N` lines name the damaged packs, and below them the files that have data in them (exit code 3).
   No password is needed for this; `--deep` also decrypts and checks every file.
2. Save what can be saved first, if the files no longer exist elsewhere:
   ```
   bdxl restore D:\Rescue "*" --mount G:\
   ```
   Only the parts of the packs each file needs are read, so a damaged spot costs only the files with data in it;
   everything else is restored. Files with a copy on another mounted disc are taken from there.
3. Mark the disc:
   ```
   bdxl lost BDXL-0005 --reason "unreadable"          (it may still turn up: 'found' undoes it)
   bdxl destroyed BDXL-0005 --reason "broken"         (final)
   ```
   Files that are still on their source become **pending again**: the next `stage` puts them on a new disc.
   Files that exist nowhere else are listed as **DATA LOST** (exit code 2).
4. Either stage the pending files normally (`bdxl stage …`), or **rebuild the same disc** to burn it again:
   ```
   bdxl restage BDXL-0005 E:\Staging --from G:\
   ```
   - The disc keeps its layout. Every pack of the damaged disc (`--from`: the disc itself, or copies of its packs)
     whose hash still matches is copied as it is; only the damaged packs are rebuilt, from the current source
     files (if their hash still matches) - so files that are gone from their source survive as long as their packs do.
   - Every rebuilt file is checked against the catalog. Without `--allow-partial`, nothing is staged if any file
     cannot be rebuilt; with it, such files are left out (and no longer count as being on the disc).
   - Burn it, then `bdxl verify BDXL-0005 G:\` and `bdxl burned BDXL-0005`.
   - Ctrl+C keeps what was rebuilt as `E:\Staging\BDXL-0005.partial`; run again with `--from E:\Staging\BDXL-0005.partial`.
5. A lost disc that turns up: `bdxl found BDXL-0005`, then `bdxl verify BDXL-0005 G:\`.

## A catalog backup is damaged

```
bdxl catalog backup verify G:\_catalog            # which files are damaged, and whether they can be repaired
bdxl catalog backup verify G:\_catalog --repair   # fix it in place
```

A backup on a burned disc cannot be changed: copy the `_catalog` folder to your disk and repair the copy.

Each file carries Reed-Solomon repair data (25% by default; `--parity-percent` when creating). No password is needed
to check or repair. A file damaged beyond its repair data is reported `UNRECOVERABLE` (exit code 3); use another
backup or another disc's `_catalog`.

## A run was interrupted (power cut, crash)

- Ctrl+C never cuts a catalog save short: it waits until the save is complete. A power cut or crash *during* a
  save can leave the catalog failing its check; roll back to the version kept before that command
  (`catalog history`, `catalog rollback <id>`), then repeat the command.
- An interrupted encryption of an old plaintext catalog is undone or finished automatically at the next start.
- An interrupted `stage` leaves a folder that is complete for what it holds (burn it or `discard`).
- An interrupted `restore` or `scan` continues when you run it again.
