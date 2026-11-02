# Getting Started

[Home](Home.md) › Getting Started

This page takes you from nothing to a first verified, burned disc.

## 1. Build bdxl

You need the .NET 10 SDK to build, and the .NET 10 runtime (part of the SDK) to run.

```
cd <repository>\bdxl
dotnet publish -p:PublishProfile=win-x64
```

The program is a single file, `<repository>\publish\bdxl.exe` (about 1.7 MB; libsodium, used for the password's
Argon2id, is inside it). Copy it anywhere you like, e.g. `C:\Tools\bdxl.exe`, and add that folder to your `PATH`.
The examples on these pages just write `bdxl`.

(`dotnet build -c Release` also works; then the program is `bdxl\bin\Release\net10.0\bdxl.exe` together with the
DLLs and the `runtimes` folder next to it, which have to stay together.)

Check it runs:

```
bdxl help
bdxl workflow        (a sample session; nothing is executed)
```

## 2. Decide where the catalog lives

The catalog is a folder that holds everything bdxl knows. Every command takes `--catalog <dir>`; without it,
`.\catalog` in the current folder is used. Put it on a disk that is itself backed up, e.g. `D:\bdxl-catalog`, and
either always run bdxl from the folder above it or always pass `--catalog D:\bdxl-catalog`.

> Tip: a small `bdxl.cmd` in your `PATH` saves typing it every time:
> `@"C:\Tools\bdxl\bdxl.exe" %* --catalog D:\bdxl-catalog`

## 3. Create the encrypted catalog

```
bdxl init --recovery-key
```

1. Create the password in your password manager and save the entry first. Paste it, then paste it again **from the
   saved entry** and confirm it is saved (see [Changing the password](Guide-Security.md#changing-the-password)).
   It needs at least 12 characters and 3 of: lowercase, uppercase, digits, special characters.
2. bdxl measures how much key derivation your computer does in about 2 seconds (Argon2id) and uses that.
3. **The recovery key is shown once.** Write it down or print it and keep it away from the computer. It replaces a
   lost password (see [Security](Guide-Security.md)). You can also add one later with `bdxl recovery-key create`.

Without the password (or the recovery key) nothing can be decrypted, ever. There is no back door.

Already have a catalog from before encryption? Any command offers to encrypt it, or run `bdxl init` in its folder.

## 4. Stop typing the password (optional)

Every command that reads the catalog needs the password. To type it once per session:

```
bdxl unlock
```

The key is now held in memory by a background agent and locks itself after 30 minutes without use, after 8 hours
at the latest, and when you lock Windows (Win+L) or the computer sleeps. `bdxl lock` locks it at once.
Details: [Security](Guide-Security.md#unlocking-for-a-while).

## 5. One-time settings (optional)

```
bdxl settings set label-prefix BDXL-       # discs are named BDXL-0001, BDXL-0002 ... (the counter never resets)
bdxl settings set currency PLN             # shown next to costs
bdxl settings set default-threads 4        # hash with 4 threads by default (helps on SSDs / several disks)
bdxl settings set mirror F:\bdxl-mirror    # a second copy of the catalog on another drive (see Catalog Mirror)
bdxl copies set --default 1                # or 2 for locations that must survive a lost disc (see Several Copies)
bdxl ignore add "Thumbs.db" "desktop.ini" "*.tmp" "node_modules/"
```

## 6. Register your blank discs

```
bdxl blanks add 10 --size 96882130944 --reserve 300MB --cost 45
```

- `--size`: the disc's capacity in bytes (`96882130944` = BD-R XL 100 GB, `128001769472` = 128 GB). Units such as
  `25GB` (decimal) or `90GiB` (binary) work too.
- `--reserve`: space kept free on every disc for the file system, the disc's own small files and, if you use
  `--with-catalog`, a catalog backup. 200–500 MB is a sensible start; a large catalog needs more (see
  [Concepts › Reserve](Concepts.md#reserve)).
- `--cost`: the price of one disc (optional; used by `bdxl cost report`).

## 7. Index your data

```
bdxl scan D:\Photos --name Photos
bdxl scan E:\Documents --name Docs
```

- `--name` is the *alias*: the name this folder has in the catalog and on discs. Default: the folder's name.
- Every file is hashed (SHA-256). This takes as long as reading all the data once; later scans only read
  files that changed.
- Ctrl+C stops cleanly; run the same command again to continue.

## 8. See the plan

```
bdxl status
bdxl plan
```

`plan` shows how many discs the pending data fills and how full each would be. It changes nothing.

## 9. Stage, burn, verify, record

```
bdxl stage E:\Staging --with-catalog
```

This writes `E:\Staging\BDXL-0001\`: the files in encrypted packs of 64 MiB (`data\000\000001.pack` …; nothing shows
their names, sizes or number), an encrypted manifest, `packs.sha256`, `disc.json`, `bdxl.key`, `README.txt` and (with
`--with-catalog`) `_catalog\`, a catalog backup.

1. Burn the **contents** of `E:\Staging\BDXL-0001` to a blank disc with your burning program (UDF 2.50 file system).
2. Write the label `BDXL-0001` on the disc.
3. With the burned disc in drive `G:`:
   ```
   bdxl verify BDXL-0001 G:\
   bdxl burned BDXL-0001
   ```
4. Delete the staging folder once the disc is verified.

That is the whole cycle. From here on, see [Backing Up](Guide-Backing-Up.md) for the routine and
[Restoring Files](Guide-Restoring.md) for getting files back.
