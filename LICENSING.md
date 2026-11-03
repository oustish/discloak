# Licensing

Discloak is licensed under the [PolyForm Shield License 1.0.0](LICENSE.md). This page says in plain words what that
means, and adds the project's rules for its name and its formats. Where this page and the license differ, the
license decides.

Required Notice: Copyright 2026 oustish (https://github.com/oustish)

## What you may do

- **Use** Discloak for any purpose - personally, at work, in a company or any other organisation - free of charge.
- **Change** it and build new works on it, for yourself or your organisation.
- **Pass on** copies, changed or not, together with the license (or its URL) and the `Required Notice:` line above.

## What needs the author's permission

Providing a product, sold or free, that **competes** with Discloak: selling Discloak or a changed version of it, offering it
as a service, or publishing a changed version as a substitute for it (the license's *Noncompete* and *Competition*
sections). For that, or for anything else the license does not cover, ask the author, oustish
(<https://github.com/oustish>), for a separate license.

## The name

The license grants no rights to the name "Discloak". A changed version that you pass on must not be called Discloak or appear
to be it: give it a name of its own, and say that it is a changed version of Discloak, and by whom.

## The formats

Discs and catalogs outlive the programs that wrote them, so their formats must not drift apart. Discloak's formats are
identified by names written into its files:

| Name | Before 0.8.0 | Where |
|---|---|---|
| `discloak-pack-1` | `bdxl-pack-1` | `disc.json` "format": the disc layout |
| `DCLKPAK1` | `BDXLPAK1` | the first bytes of every pack (`data/NNN/NNNNNN.pack`) |
| `DCLKENC2` | `BDXLENC2` | the first bytes of the catalog's files and of a disc's manifests |
| `discloak-key-2` | `bdxl-key-2`, `bdxl-key-1` | the key file's "format" (`discloak.key`; `bdxl.key` before 0.8.0) |

Discloak was called bdxl before version 0.8.0; the older names are those of that time, and are protected the same way.

A changed version must not write files under these names unless Discloak reads them exactly as it reads its own. A
changed format gets a name of its own, not beginning with "discloak", "DCLK", "bdxl" or "BDXL". And a changed version
does not record itself as Discloak (or bdxl) in a disc's "createdBy".

The formats are documented - [Files and Formats](docs/Files-and-Formats.md), [`discloak/ENCRYPTION.md`](discloak/ENCRYPTION.md)
and the `README.txt` on every disc - so that discs stay readable with or without Discloak. Reading them with tools of
your own is always fine.

## Other people's code in Discloak

`discloak.exe` contains components under their own licenses (MIT, ISC, BSD): see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which goes with every copy of the program.
