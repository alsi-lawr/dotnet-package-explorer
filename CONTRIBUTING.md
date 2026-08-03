# Contributing

Keep changes focused and verify the smallest relevant target before running the full solution.

## Set up

The Nix development shell provides the selected .NET SDK, Git, Fantomas 7.0.5, and
FsAutoComplete:

```console
nix develop
```

Without Nix, install the SDK selected by `global.json`, then restore the repository tool and
packages:

```console
dotnet tool restore
dotnet restore Dotnet.PackageExplorer.slnx
```

## Build and test

Build only the project you changed while iterating. Use the solution for a final local check:

```console
dotnet build src/Application/Dotnet.PackageExplorer.Application.fsproj
dotnet build Dotnet.PackageExplorer.slnx
```

Run the nearest Microsoft Testing Platform executable. The foundation test target currently
contains one smoke scenario:

```console
dotnet tests/unit/Foundation/bin/Debug/net10.0/Dotnet.PackageExplorer.Foundation.UnitTests.dll
```

Use FsUnit assertions and full behavioral scenario identifiers in F# tests.

## Format

Format the repository with the pinned Fantomas version:

```console
fantomas .
```

Check formatting without changing files:

```console
fantomas --check .
```

Outside the Nix shell, use `dotnet fantomas` after restoring the tool manifest.

## Clear F# diagnostics

Before submitting an F# change, use FsAutoComplete to inspect every F# file you touched. Clear all
applicable warnings and suggestions before committing.

Start one FsAutoComplete server for the workspace, inspect the touched files sequentially, then
send the language-server shutdown and exit messages. Confirm that no FsAutoComplete process
remains. Never run language-server instances in parallel.

FsAutoComplete is a local implementation and review tool. Do not add it to GitHub Actions, a
Neovim test dependency, or a repository diagnostics harness.
