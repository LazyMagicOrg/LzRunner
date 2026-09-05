# LzRunner

The `lz` dispatcher. A thin global .NET tool that resolves the correct
`Lz.Cli.*.nupkg` for the current working directory (via NuGet.Config
walk) and invokes it via `dotnet <Lz.Cli.dll> <args>`.

The infrastructure logic — `Lz.Cli`, `Lz.Aws`, `Lz.Azure`, `Lz.Core`,
`Lz.Gen` — lives in the sibling [Lz](https://github.com/LazyMagicOrg/Lz)
repo and is published as per-tenant NuGet packages.

See `Platform/LzRunnerSplit.md` in any Monro working copy for the full
architecture writeup.

## Install

```
dotnet tool install -g Lz.Runner \
  --add-source <path-to-this-repo/Packages>
```

(Or `--add-source` pointing at a published feed once the package is
there.) To move an existing install to a newer build, use
`dotnet tool update -g Lz.Runner --add-source <path-to-this-repo/Packages>`
while no `lz` process is running.

## Resolution

Across every local feed in scope the runner picks the **highest-versioned**
`Lz.Cli.<version>.nupkg`, with versions parsed and ordered the way NuGet
orders them (NuGet.Versioning, SemVer 2): `0.11.1 < 0.11.2-alpha.1 <
0.11.2-alpha.2 < 0.11.2-beta.1 < 0.11.2`, numeric prerelease identifiers
compare numerically (`alpha.10` beats `alpha.9`), and build metadata
(`+g1a2b3c`) never affects the order. A tie on version keeps the first feed
enumerated, and feeds are enumerated in the order the config chain is walked
(machine, user, then drive root down to cwd), so the OUTERMOST config's feed
wins a tie - the same feed runner 1.3.0 chose. This is what a `*-*` float would resolve
to, and it is what a developer building the framework locally wants:
their own newest package wins, prerelease or not.

Runner 1.3.0 and earlier filtered candidates with `System.Version`, which
rejects anything carrying a prerelease label — so a feed holding only
derived versions resolved to nothing and `lz` failed in every workspace on
the machine at once. Fixed in 1.4.0. Remote sources are still ignored by
design; registry resolution is a separate, undecided piece of work.

## Extraction cache

The resolved nupkg is unpacked once and reused. Location:
`%LOCALAPPDATA%\lz-runner\cache` on Windows, `~/.local/share/lz-runner/cache`
(`$XDG_DATA_HOME`) on Linux, `~/Library/Application Support/lz-runner/cache`
on macOS. `LZ_RUNNER_VERBOSE=1` prints the exact entry in use. Layout:

```
cache/
├── 0.11.1/                                  runner ≤1.2.0 layout — inert leftovers
└── 0.11.1+82752768-639239760291128396/      <version>+<length>-<mtimeUtcTicks>: one entry per distinct nupkg
    ├── .source.json                         which nupkg it came from; "complete" sentinel
    └── tools/<tfm>/any/Lz.Cli.dll
```

Entries are keyed by the nupkg's **identity** (normalized version + length + mtime;
the normalized version drops build metadata, so it never contains the `+` separator),
not by version alone. Several working copies on one machine routinely
pack different bits under the same `LzVersion`, and each one gets its own
entry, so switching between working copies never rebuilds or deletes
anything, and an `lz` run in one working copy can never disturb an `lz`
run in another. (Runner 1.2.0 kept a single folder per version and
rebuilt it in place on a mismatch; that rebuild deleted the folder out
from under whatever `lz` process the *other* working copy still had
running from it.)

Extraction goes into a private staging directory
(`<identity>.tmp-<pid>`) that is renamed into place only when complete,
so an entry is either whole or absent. The rename is retried for a couple
of seconds because Windows antivirus and indexing briefly hold freshly
written files. Publishers of one entry take a short-lived lock file
(`<identity>.lock`) around the rename, so two `lz` processes racing to
extract the same nupkg both succeed. Staging or retired directories left
by a runner that died part-way are swept up by a later run once their
pid is gone.

Nothing is ever pruned automatically; the cache only grows by one
extracted tree (~280 MB for Lz.Cli) per distinct nupkg. Delete the whole
`cache` directory at any quiet moment (no `lz` running) to reclaim
space. The `<version>/` directories that runner 1.2.0 extracted into are
inert leftovers; 1.3.0 entries sit beside them, never inside, so even a
rolled-back 1.2.0 runner rebuilding its folder cannot disturb them.

## Entry-point contract with Lz.Cli

Before invoking `dotnet <Lz.Cli.dll>`, the runner sets three env vars
that the `Lz.Cli` `--version` handler reads:

| Env var | Meaning |
|---|---|
| `LZ_RUNNER_VERSION` | The runner's own version (e.g. `1.0.0`) |
| `LZ_RUNNER_NUPKG_PATH` | Absolute path to the resolved `Lz.Cli.*.nupkg` file |
| `LZ_RUNNER_FEED` | The local feed directory that nupkg came from |

This is the entire contract. Keep it stable across Lz.Cli releases.

## Verbose mode

Set `LZ_RUNNER_VERBOSE=1` to see the NuGet.Config chain, the effective
local feeds tried, and which one provided the resolved `Lz.Cli.*.nupkg`.

## Build

```
dotnet build LzRunner.slnx -c Release
dotnet test  LzRunner.slnx
```

`CommonPackageHandling.targets` publishes the resulting nupkg into
`Packages/` on every **Release** build; Debug builds (including `dotnet test`)
leave the shipped package alone. Always build and test through the `.slnx`:
`Lz.Runner.csproj` imports the targets via `$(SolutionDir)`, which a
project-level command does not set (`MSB4019`). To install what you built:

```
dotnet tool update -g Lz.Runner --add-source .\Packages
```

from this folder (a workspace root that uses package-source mapping refuses
`--add-source`). If the same version was installed before, evict
`%USERPROFILE%\.nuget\packages\lz.runner\<version>` first - a same-version
repack is otherwise served unchanged from that cache.
