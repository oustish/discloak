# Discloak

**Encrypted, self-describing backups on BDXL and other optical discs - planned, staged, verified and catalogued.**

Discloak keeps a catalog of your files (every file's SHA-256, where its copies are), plans which files go on which
blank disc, and writes each disc's folder, ready for your burning program. On the disc, every file is laid into one
stream of data, cut into equal 64 MiB packs, each encrypted on its own: a disc shows no file names, no sizes and not
even how many files it holds. Discs can be read back and checked years later - with or without the catalog, and even
without Discloak installed: every disc carries the program that wrote it, its source code, and a README describing
the formats.

> Status: **pre-1.0** (see the [releases](https://github.com/oustish/discloak/releases)). The disc and catalog formats
> are stable, and every version keeps reading what older ones wrote. Windows x64. Formerly called *bdxl* (before 0.8.0).

## Features

- **Catalog**: every file with its hash, encrypted with a random master key that your password (Argon2id) and an
  optional recovery key unlock; integrity checked on every start (HMAC), version history and rollback, a mirror on
  another drive, backups with Reed-Solomon repair data, key rotation, an `unlock` agent so the password is not
  typed for every command.
- **Planning**: discs filled exactly to their capacity, fullest first; files larger than a disc split across
  discs; several copies per location; duplicates stored once; compression per location (zstd); sessions appended
  to a burned disc.
- **Discs**: AES-256-GCM in equal packs, an encrypted manifest padded to whole packs, `packs.sha256` to check a disc
  with any `sha256sum`, an optional catalog backup on the disc, and the program itself (`_program\`). Staged discs
  waiting to be burned are brought up to date with `stage refresh` (the latest catalog backup and program).
- **Checking**: `verify` compares every pack's hash with the catalog; `--deep` decrypts every file and checks its
  hash; `--packs 312` checks single packs; `--no-cache` reads past the Windows cache. A read that fails is tried
  again from the disc before it counts as damage (and reported as a warning sign). Copies found damaged are recorded
  and stop counting, so the next `stage` backs those files up again; a later verify that reads them back intact
  clears the mark. `--mark-burned` records the disc as burned once everything checked out.
- **Recovery**: `restore` from any mounted copy (a damaged spot costs only the files in it), damaged packs burned
  again as a new session onto a disc left open until it passed `verify`, `restage` (rebuild a disc, reusing its intact
  packs), `scandisc` (rebuild a lost catalog from the discs).

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
discloak stage refresh E:\Staging                          # just before burning (if staged a while ago)
   ... burn E:\Staging\DISC-0001 with your burning program (do not finalize it until verify passes) ...
discloak verify DISC-0001 G:\ --deep --mark-burned         # read it back, check every file, record it as burned
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
