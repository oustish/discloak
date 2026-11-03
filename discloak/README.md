# Discloak - archive planner and catalog for BDXL discs

Plans which files go on which blank disc, stages encrypted disc folders (the files laid into equal 64 MiB packs,
each encrypted with the catalog's master key, so a disc shows no file names, sizes or count; optional zstd
compression per location), and keeps an encrypted catalog of every file, hash and disc. One password (changeable,
with an optional recovery key and key rotation) protects everything.

**Documentation:** [`../docs/Home.md`](../docs/Home.md) - getting started, step-by-step guides, command reference,
formats, troubleshooting. Design: [`ENCRYPTION.md`](ENCRYPTION.md). Developer notes: [`HANDOFF.md`](HANDOFF.md).
Working on it (build, tests, versions, commits): [`../CONTRIBUTING.md`](../CONTRIBUTING.md). What changed in each
version: [`../CHANGELOG.md`](../CHANGELOG.md); `discloak version` shows the version of an exe.
License: [PolyForm Shield 1.0.0](../LICENSE.md) - free to use, not to compete with; see
[`../LICENSING.md`](../LICENSING.md) (also the name and format rules) and
[`../THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

    dotnet publish -p:PublishProfile=win-x64   one file: ../publish/discloak.exe (needs the .NET 10 SDK; runs on the .NET 10 runtime)
    dotnet build -c Release          (or a normal build: bin/Release/net10.0/, exe + DLLs)
    dotnet run -- help               list of commands
    dotnet run -- workflow           sample session
    dotnet run -- <command> --help   details for one command

## Layout
    src/Program.cs            entry point: unlock + load the catalog, run the command
    src/Cli/                  option parsing, command table + help, prompts, Ctrl+C handling
    src/Model/                records stored in the catalog, settings
    src/Storage/              JSON lines, hashing, the encrypted catalog (integrity HMAC + version history)
    src/Core/                 master key / key ring / key file, Argon2id, encryption (DCLKENC2 for the catalog and
                              manifests, packs for disc data), stored form + compression, packer, .gitignore matcher,
                              Reed-Solomon, size parsing
    src/Services/             Ctx, Archive (dedup, planning, labels, ignore rules), disc files, catalog setup
    src/Agent/                the background agent behind 'unlock'
    src/Commands/             one file per command

## Catalog folder (default ./catalog)
    discloak.key                                                  the master key(s), wrapped by password / recovery key
    files.NNN.jsonl.enc  discs.jsonl.enc  blanks.jsonl.enc  settings.json.enc   the data, encrypted
    catalog.hash.json                                         hashes of the files + HMAC, checked on every start
    history/<time>-<command>/                                 previous version, one per command that changed something
