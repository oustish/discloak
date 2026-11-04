# Discloak

**Encrypted, self-describing backups on BDXL and other optical discs - planned, staged, verified and catalogued.**

Discloak keeps a catalog of your files (every file's SHA-256, where its copies are), plans which files go on which
blank disc, and writes each disc's folder, ready for your burning program. On the disc, every file is laid into one
stream of data, cut into equal 64 MiB packs, each encrypted on its own: a disc shows no file names, no sizes and not
even how many files it holds. Discs can be read back and checked years later - with or without the catalog, and even
without Discloak installed: every disc carries the program that wrote it, its source code, and a README describing
the formats.

> Status: **pre-1.0** (0.9.x). The disc and catalog formats are stable, and every version keeps reading what older
> ones wrote. Windows x64. Formerly called *bdxl* (before 0.8.0).

## Features

- **Catalog**: every file with its hash, encrypted with a random master key that your password (Argon2id) and an
  optional recovery key unlock; integrity checked on every start (HMAC), version history and rollback, a mirror on
  another drive, backups with Reed-Solomon repair data, key rotation, an `unlock` agent so the password is not
  typed for every command.
- **Planning**: discs filled exactly to their capacity, fullest first; files larger than a disc split across
  discs; several copies per location; duplicates stored once; compression per location (zstd); sessions appended
  to a burned disc.
- **Discs**: AES-256-GCM in equal packs, an encrypted manifest padded to whole packs, `packs.sha256` to check a disc
  with any `sha256sum`, an optional catalog backup on the disc, and the program itself (`_program\`).
- **Checking and recovery**: `verify` (hashes, `--deep` decrypts every file, `--no-cache` reads past the Windows
  cache), `restore` from any mounted copy (a damaged spot costs only the files in it), `restage` (rebuild a disc,
  reusing its intact packs), `scandisc` (rebuild a lost catalog from the discs).

## Getting it

- **Download**: the `discloak-<version>-win-x64.zip` of a [release](https://github.com/oustish/discloak/releases)
  holds `discloak.exe` - one self-contained file, nothing to install - and its licenses.
- **Build**: with the .NET 10 SDK and git,
  ```
  cd discloak
  dotnet publish -p:PublishProfile=win-x64        # -> ..\publish\discloak.exe
  ```

## In one minute

```
discloak init --recovery-key                               # once: a new encrypted catalog
discloak blanks add 10 --size 96882130944 --reserve 300MB  # the blank discs you have (exact bytes)
discloak scan D:\Photos --name Photos                      # index a folder
discloak stage E:\Staging --with-catalog                   # the next disc's folder: E:\Staging\DISC-0001
   ... burn E:\Staging\DISC-0001 with your burning program (UDF 2.50) ...
discloak verify DISC-0001 G:\ --deep                       # read the burned disc back and check every file
discloak burned DISC-0001
discloak restore D:\Recovered "holiday" --mount G:\        # later: get files back
```

## Documentation

- [Getting started](docs/Getting-Started.md), then the [documentation home](docs/Home.md): guides for backing up,
  restoring, disaster recovery, appending, large files, copies, compression, maintenance and security.
- [Command reference](docs/Command-Reference.md), [files and formats](docs/Files-and-Formats.md),
  [troubleshooting](docs/Troubleshooting.md).
- Design: [encryption and disc format](discloak/ENCRYPTION.md). Developer notes: [handoff](discloak/HANDOFF.md).
- [Changelog](CHANGELOG.md).

## License

[PolyForm Shield 1.0.0](LICENSE.md): free to use for any purpose, at home or at work; not to build a product that
competes with Discloak. [LICENSING.md](LICENSING.md) explains it in plain words and sets the rules for the name and
the disc formats; [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists the components inside `discloak.exe`.

Contributions: [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: [SECURITY.md](SECURITY.md) - please report them
privately, not as issues.

Blu-ray Disc and BDXL are trademarks of the Blu-ray Disc Association; Discloak is not affiliated with it.
