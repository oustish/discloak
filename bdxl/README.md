# bdxl - BDXL archive planner and catalog

Plans which files go on which blank disc, stages encrypted disc folders (every file encrypted on its own with the
catalog's master key - no archive, optional zstd compression per location), and keeps an encrypted catalog of
every file, hash and disc. One password (changeable, with an optional recovery key and key rotation) protects
everything.

**Documentation:** [`../docs/Home.md`](../docs/Home.md) - getting started, step-by-step guides, command reference,
formats, troubleshooting. Design: [`ENCRYPTION.md`](ENCRYPTION.md). Developer notes: [`HANDOFF.md`](HANDOFF.md).

    dotnet build -c Release          (needs the .NET 10 SDK)
    dotnet run -- help               list of commands
    dotnet run -- workflow           sample session
    dotnet run -- <command> --help   details for one command

## Layout
    src/Program.cs            entry point: unlock + load the catalog, run the command
    src/Cli/                  option parsing, command table + help, prompts, Ctrl+C handling
    src/Model/                records stored in the catalog, settings
    src/Storage/              JSON lines, hashing, the encrypted catalog (integrity HMAC + version history)
    src/Core/                 master key / key ring / key file, Argon2id, file encryption (BDXLENC2), compression,
                              packer, .gitignore matcher, Reed-Solomon, size parsing
    src/Services/             Ctx, Archive (dedup, planning, labels, ignore rules), disc files, catalog setup
    src/Agent/                the background agent behind 'unlock'
    src/Commands/             one file per command

## Catalog folder (default ./catalog)
    bdxl.key                                                  the master key(s), wrapped by password / recovery key
    files.jsonl.enc  discs.jsonl.enc  blanks.jsonl.enc  settings.json.enc   the data, encrypted
    catalog.hash.json                                         hashes of the files + HMAC, checked on every start
    history/<time>-<command>/                                 previous version, one per command that changed something
