# Getting Started

[Home](Home.md) › Getting Started

This page takes you from nothing to a first verified, burned disc.

## 1. Build discloak

You need the .NET 10 SDK and git to build. The built program needs nothing installed to run.

```
cd <repository>\discloak
dotnet publish -p:PublishProfile=win-x64
```

The program is a single file, `<repository>\publish\discloak.exe` (about 37 MB: the .NET runtime and libsodium, used
for the password's Argon2id, are inside it, so it runs on any 64-bit Windows with nothing installed; every disc gets a
copy of it). Copy it anywhere you like, e.g. `C:\Tools\discloak.exe`, and add that folder to your `PATH`.
The examples on these pages just write `discloak`.

(`dotnet build -c Release` also works; then the program is `discloak\bin\Release\net10.0\discloak.exe` together with the
DLLs and the `runtimes` folder next to it, which have to stay together.)

Check it runs:

```
discloak help
discloak workflow        (a sample session; nothing is executed)
```

## 2. Decide where the catalog lives

The catalog is a folder that holds everything discloak knows. Every command takes `--catalog <dir>`; without it,
`.\catalog` in the current folder is used. Put it on a disk that is itself backed up, e.g. `D:\discloak-catalog`, and
either always run discloak from the folder above it or always pass `--catalog D:\discloak-catalog`.

> Tip: a small `discloak.cmd` in your `PATH` saves typing it every time:
> `@"C:\Tools\discloak\discloak.exe" %* --catalog D:\discloak-catalog`

## 3. Create the encrypted catalog

```
discloak init --recovery-key
```

1. Create the password in your password manager and save the entry first. Paste it, then paste it again **from the
   saved entry** and confirm it is saved (see [Changing the password](Guide-Security.md#changing-the-password)).
   It needs at least 12 characters and 3 of: lowercase, uppercase, digits, special characters.
2. discloak measures how much key derivation your computer does in about 2 seconds (Argon2id) and uses that.
3. **The recovery key is shown once.** Write it down or print it and keep it away from the computer. It replaces a
   lost password (see [Security](Guide-Security.md)). You can also add one later with `discloak recovery-key create`.

Without the password (or the recovery key) nothing can be decrypted, ever. There is no back door.

Already have a catalog from before encryption? Any command offers to encrypt it, or run `discloak init` in its folder.

## 4. Stop typing the password (optional)

Every command that reads the catalog needs the password. To type it once per session:

```
discloak unlock
```

The key is now held in memory by a background agent and locks itself after 30 minutes without use, after 8 hours
at the latest, and when you lock Windows (Win+L) or the computer sleeps. `discloak lock` locks it at once.
Details: [Security](Guide-Security.md#unlocking-for-a-while).

## 5. One-time settings (optional)

```
discloak settings set label-prefix DISC-       # discs are named DISC-0001, DISC-0002 ...
discloak settings set currency PLN             # shown next to costs
discloak settings set default-threads 4        # hash with 4 threads by default (helps on SSDs / several disks)
discloak settings set mirror F:\discloak-mirror    # a second copy of the catalog on another drive (see Catalog Mirror)
discloak copies set --default 1                # or 2 for locations that must survive a lost disc (see Several Copies)
discloak ignore add "Thumbs.db" "desktop.ini" "*.tmp" "node_modules/"
```

## 6. Register your blank discs

```
discloak blanks add 10 --size 96882130944 --reserve 300MB --cost 45
```

- `--size`: the disc's capacity in bytes (`96882130944` = BD-R XL 100 GB, `128001769472` = 128 GB). Units such as
  `25GB` (decimal) or `90GiB` (binary) work too.
- `--reserve`: space kept free on every disc for the file system, the disc's own small files and, if you use
  `--with-catalog`, a catalog backup. 200–500 MB is a sensible start; a large catalog needs more (see
  [Concepts › Reserve](Concepts.md#reserve)).
- `--cost`: the price of one disc (optional; used by `discloak cost report`).

## 7. Index your data

```
discloak scan D:\Photos --name Photos
discloak scan E:\Documents --name Docs
```

- `--name` is the *alias*: the name this folder has in the catalog and on discs. Default: the folder's name.
- Every file is hashed (SHA-256). This takes as long as reading all the data once; later scans only read
  files that changed.
- Ctrl+C stops cleanly; run the same command again to continue.

## 8. See the plan

```
discloak status
discloak plan
```

`plan` shows how many discs the pending data fills and how full each would be. It changes nothing.

## 9. Stage, burn, verify, record

```
discloak stage E:\Staging --with-catalog
```

This writes `E:\Staging\DISC-0001\`: the files in encrypted packs of 64 MiB (`data\000\000001.pack` …; nothing shows
their names, sizes or number), an encrypted manifest, `packs.sha256`, `disc.json`, `discloak.key`, `README.txt` and (with
`--with-catalog`) `_catalog\`, a catalog backup.

1. Burn the **contents** of `E:\Staging\DISC-0001` to a blank disc with your burning program, with a file system
   the disc supports (the program's default for the medium is usually right; see
   [Troubleshooting](Troubleshooting.md#file-system)).
   **Do not finalize (close) the disc yet**: leave it open until `verify` passes. Then, if a pack fails, it can be
   burned again on its own as a new session ([Backing Up](Guide-Backing-Up.md#if-verify-finds-a-damaged-pack)).
2. Write the label `DISC-0001` on the disc.
3. With the burned disc in drive `G:`:
   ```
   discloak verify DISC-0001 G:\ --deep
   discloak burned DISC-0001
   ```
   (or `discloak verify DISC-0001 G:\ --deep --mark-burned`: it marks the disc as burned only if everything checks out)
4. Once the disc is verified: finalize it if you want (or leave it open to [add more later](Guide-Appending.md)), and
   delete the staging folder.

That is the whole cycle. From here on, see [Backing Up](Guide-Backing-Up.md) for the routine and
[Restoring Files](Guide-Restoring.md) for getting files back.
