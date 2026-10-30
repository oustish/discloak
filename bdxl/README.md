# bdxl - BDXL archive planner and catalog

Plans which files go on which blank disc, stages encrypted disc folders (every file encrypted on its own with the
catalog's master key - no archive, no compression), and keeps an encrypted catalog of every file, hash and disc.
One password (changeable, with an optional recovery key) protects everything; see ENCRYPTION.md.

    dotnet build -c Release          (needs the .NET 10 SDK)
    dotnet run -- help               list of commands
    dotnet run -- workflow           sample session
    dotnet run -- <command> --help   details for one command

## Layout
    src/Program.cs            entry point
    src/Cli/                  option parsing, command table + help, prompts
    src/Model/                records stored in the catalog, settings
    src/Storage/              JSON lines, hashing, catalog (integrity hash + version history)
    src/Core/                 packer, .gitignore matcher, AES-GCM file encryption, Reed-Solomon, size parsing
    src/Services/             Ctx (what a command receives), Archive (dedup, planning, labels, ignore rules)
    src/Commands/             one file per command group

## Catalog folder (default ./catalog)
    files.jsonl  discs.jsonl  blanks.jsonl  settings.json     the data (settings = prefix, currency, ignore rules ...)
    catalog.hash.json                                         SHA-256 of the four files, checked on every start
    history/<time>-<command>/                                 previous version, one per command that changed something
