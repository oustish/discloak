# bdxl 0.7.1 fixture

A catalog and a disc as **bdxl 0.7.1** wrote them, before the program was renamed to Discloak (0.8.0). The tests use
them to prove that Discloak still reads what bdxl wrote, with the old names: `bdxl.key`, `bdxl-pack-1`, `bdxl-key-2`,
`BDXLPAK1` (packs) and `BDXLENC2` (catalog files, manifests).

Made on 2026-11-04 by the code of commit `eab32e3` (plus the one-off test that wrote this folder, hence `-dirty` in
`disc.json` "createdBy"), with the tests' small packs: 8 chunks of 8 KiB.

| Folder | What |
|---|---|
| `catalog/` | the catalog after `burned BDXL-0001` (without `history/`). Password: `TestKeys.Password`, Argon2id at its lowest settings |
| `disc/` | the disc `BDXL-0001`, staged with `--with-catalog` (its `_catalog` was written before `burned`) |

The source files are gone; the tests make them again from these rules:

| File | Content |
|---|---|
| `Photos/a.bin` | 100,000 random bytes, `new Random(11)` (packs 1-2) |
| `Photos/b.bin` | 20,000 random bytes, `new Random(12)` |
| `Docs/notes.txt` | `"invoice total 2024 amount paid\r\n"` 1,000 times, stored compressed (zstd level 3) |

Never change these files: they must stay byte for byte as bdxl wrote them. `.gitattributes` keeps git from changing
their line endings.
