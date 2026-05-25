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
dotnet tool install -g Lz.Runner --version 1.0.0 \
  --add-source <path-to-this-repo/Packages>
```

(Or `--add-source` pointing at a published feed once `Lz.Runner.1.0.0`
is there.)

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
```

`CommonPackageHandling.targets` auto-publishes the resulting nupkg into
`Packages/` on every build.
