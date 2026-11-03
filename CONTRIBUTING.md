# Working on bdxl

The app is in [`bdxl/`](bdxl/), its tests in [`tests/Bdxl.Tests/`](tests/Bdxl.Tests/), the user documentation in
[`docs/`](docs/Home.md). [`bdxl/HANDOFF.md`](bdxl/HANDOFF.md) is the map of the code: where things are and the rules
they follow. [`bdxl/ENCRYPTION.md`](bdxl/ENCRYPTION.md) is the design of the encryption and the disc format.

## Build, test, publish

From the repository root:

```
dotnet build BdxlPlanner.slnx -c Release
dotnet test BdxlPlanner.slnx -c Release                    # all tests, a few seconds
cd bdxl && dotnet publish -p:PublishProfile=win-x64         # one file: publish/bdxl.exe
```

`git` must be on the PATH when building: the build records the commit, the branch, whether there were uncommitted
changes, and the build time (see [Versions](#versions)). Without git it still builds, as "commit unknown".

When testing by hand, never use the real catalog or the real agent: give a scratch folder with `--catalog`, and set
`BDXL_AGENT=off`.

## Every change

- **Tests**: a change comes with tests that fail without it. The tests use small packs (8 chunks of 8 KiB, set in
  `TestUtil.cs`) and the cheapest password hashing. Tests that read what a command prints, or set `BDXL_AGENT`, are in
  the non-parallel collection (`[Collection(nameof(InterruptTests))]`): `Console.Out` and `Interrupt` are the whole
  process's.
- **Docs**: the command's help text (`Details`), the pages under `docs/` it touches, `bdxl/HANDOFF.md` (the code map
  and the test table), and [`CHANGELOG.md`](CHANGELOG.md).
- **Formats**: a disc format that has been burned stays readable by every later version (today `bdxl-pack-1`, and
  `bdxl-key-1`/`bdxl-key-2` key files). New fields are optional: an older bdxl ignores them, and a newer one treats a
  missing field as the old behaviour (example: a compressed copy without `codec` is zstd).

## Versions

`major.minor.patch` ([semver](https://semver.org)), in one place: `<Version>` in [`bdxl/Bdxl.csproj`](bdxl/Bdxl.csproj).
It is raised by hand, in the commit that makes the change, and the commit message says so:

| Part | Raised for | Example |
|---|---|---|
| major | a disc, catalog or key file format that older versions cannot read; a command or option removed or renamed | a second compression codec (older versions would not read those copies) |
| minor | a new command, option or feature; a new field that older versions ignore | `verify --no-cache`, the disc checks of `verify` |
| patch | fixes, messages and output, speed, docs | a wrong progress percentage |

The lower parts go back to 0 (0.6.3 → 0.7.0). Several changes before a publish need only the highest raise among them.
1.0.0 comes when the formats and commands are settled for good.

The build adds the rest (the `BdxlBuildInfo` target in `Bdxl.csproj`): the full version is
`0.6.0+8082b22.20261103T2215Z` - the commit, `-dirty` after it when there were uncommitted changes, and the build time
(UTC). `bdxl version` shows it, and every staged session records it (`disc.json` "createdBy", the catalog's
`WrittenBy`), so a disc says which build made it.

**Publishing** a version: raise the version (if the changes since the last publish have not already), add its
section to `CHANGELOG.md`, commit, publish, and tag the commit: `git tag v0.6.0`, `git push --tags`. An exe built from
uncommitted changes shows `*` in `bdxl version` (and `-dirty` on its discs).

## License

bdxl is under the [PolyForm Shield License 1.0.0](LICENSE.md); [LICENSING.md](LICENSING.md) says in plain words what
it allows, and holds the rules for the name "bdxl" and the format names. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
has the licenses of the components inside `bdxl.exe`; a new dependency adds its notice there, word for word from its
package or project. `dotnet publish` puts all three files next to `bdxl.exe`; they go with every copy given away.

By contributing a change you agree that it is distributed under the project's license, and that the author may also
license it under other terms (so that separate licenses can be granted for the whole program).

## Commits

- One change per commit, the message saying what changed for the user (the first line) and why (below).
- Commits written with Claude end with `Co-Authored-By: Claude …`.
- Work goes to `master`, pushed to the origin.
