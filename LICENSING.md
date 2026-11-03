# Licensing

bdxl is licensed under the [PolyForm Shield License 1.0.0](LICENSE.md). This page says in plain words what that
means, and adds the project's rules for its name and its formats. Where this page and the license differ, the
license decides.

Required Notice: Copyright 2026 oustish (https://github.com/oustish)

## What you may do

- **Use** bdxl for any purpose - personally, at work, in a company or any other organisation - free of charge.
- **Change** it and build new works on it, for yourself or your organisation.
- **Pass on** copies, changed or not, together with the license (or its URL) and the `Required Notice:` line above.

## What needs the author's permission

Providing a product, sold or free, that **competes** with bdxl: selling bdxl or a changed version of it, offering it
as a service, or publishing a changed version as a substitute for it (the license's *Noncompete* and *Competition*
sections). For that, or for anything else the license does not cover, ask the author, oustish
(<https://github.com/oustish>), for a separate license.

## The name

The license grants no rights to the name "bdxl". A changed version that you pass on must not be called bdxl or appear
to be it: give it a name of its own, and say that it is a changed version of bdxl, and by whom.

## The formats

Discs and catalogs outlive the programs that wrote them, so their formats must not drift apart. bdxl's formats are
identified by names written into its files:

| Name | Where |
|---|---|
| `bdxl-pack-1` | `disc.json` "format": the disc layout |
| `BDXLPAK1` | the first bytes of every pack (`data/NNN/NNNNNN.pack`) |
| `BDXLENC2` | the first bytes of the catalog's files and of a disc's manifests |
| `bdxl-key-1`, `bdxl-key-2` | `bdxl.key` "format" |

A changed version must not write files under these names unless bdxl reads them exactly as it reads its own. A
changed format gets a name of its own, not beginning with "bdxl". And a changed version does not record itself as bdxl
in a disc's "createdBy".

The formats are documented - [Files and Formats](docs/Files-and-Formats.md), [`bdxl/ENCRYPTION.md`](bdxl/ENCRYPTION.md)
and the `README.txt` on every disc - so that discs stay readable with or without bdxl. Reading them with tools of
your own is always fine.

## Other people's code in bdxl

`bdxl.exe` contains components under their own licenses (MIT, ISC, BSD): see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which goes with every copy of the program.
