# Contributing

Keep changes focused and verify the smallest relevant target before running the full solution.

## Set up

The Nix development shell provides .NET, Git, Fantomas 7.0.5, and FsAutoComplete:

```console
nix develop
```

Without Nix, install the SDK selected by `global.json`, then restore the tools and packages:

```console
dotnet tool restore
dotnet restore Dotnet.PackageExplorer.slnx
```

## Build and test

Build the complete solution in Release mode:

```console
dotnet build Dotnet.PackageExplorer.slnx --configuration Release --no-restore
```

Run each Microsoft Testing Platform executable directly:

```console
dotnet tests/unit/Application/bin/Release/net10.0/\
Dotnet.PackageExplorer.Application.UnitTests.dll
dotnet tests/unit/RpcClient/bin/Release/net10.0/\
Dotnet.PackageExplorer.RpcClient.UnitTests.dll
dotnet tests/unit/Terminal/bin/Release/net10.0/\
Dotnet.PackageExplorer.Terminal.UnitTests.dll
dotnet tests/integration/RpcClient/bin/Release/net10.0/\
Dotnet.PackageExplorer.RpcClient.IntegrationTests.dll
dotnet tests/integration/Terminal/bin/Release/net10.0/\
Dotnet.PackageExplorer.Terminal.IntegrationTests.dll
```

Use FsUnit assertions and full behavioral scenario identifiers in F# tests.

## Format and diagnostics

Check formatting with the pinned Fantomas version:

```console
git ls-files -z '*.fs' | xargs -0 fantomas --check
```

Outside Nix, replace `fantomas` with `dotnet fantomas` after restoring the tool manifest.

Before submitting an F# change, start exactly one FsAutoComplete server for the workspace. Inspect
all touched F# files one at a time, clear applicable warnings and suggestions, then send shutdown
and exit. Confirm no FsAutoComplete process remains. FsAutoComplete is a local check, not a CI or
editor dependency.

## Nix package

Build and run the default package:

```console
nix build
nix run . -- MySolution.slnx
```

Test an isolated profile installation with:

```console
nix profile install path:. --profile artifacts/nix-profile
artifacts/nix-profile/bin/dotnet-pe MySolution.slnx
```

The Nix package does not include Workspace Explorer. Runtime checks still need a compatible
`dotnet we` installation.

## .NET tool package

Create and inspect the .NET tool and symbol packages:

```console
dotnet pack src/Terminal/Dotnet.PackageExplorer.Terminal.fsproj \
  --configuration Release --no-build --output artifacts/packages
unzip -l artifacts/packages/ALSI.PackageExplorer.0.1.0.nupkg
unzip -l artifacts/packages/ALSI.PackageExplorer.0.1.0.snupkg
```

Test both command forms from an isolated installation:

```console
dotnet tool install ALSI.PackageExplorer \
  --tool-path artifacts/tool --add-source artifacts/packages --version 0.1.0
artifacts/tool/dotnet-pe MySolution.slnx
PATH="$PWD/artifacts/tool:$PATH" dotnet pe MySolution.slnx
```

## Self-contained application

`linux-x64` is the only locally verified self-contained target. Publish it with:

```console
dotnet publish src/Terminal/Dotnet.PackageExplorer.Terminal.fsproj \
  --configuration Release --property PublishProfile=linux-x64 \
  --output artifacts/publish/linux-x64
artifacts/publish/linux-x64/dotnet-pe MySolution.slnx
```

This includes the Package Explorer application runtime only. Testing still needs `dotnet` and a
compatible, separately installed Workspace Explorer backend.

## Showcase

The moving showcase uses the local VHS fork and a disposable F# project. Make sure
`dotnet pe` and `dotnet we` resolve, then run:

```console
VHS_SOURCE=/home/alex/dev/vhs ./showcase/capture.sh
```

Set `VHS_BIN` instead to use an already built binary from that fork. The script replaces
`docs/showcase/package-explorer.webp` and removes its temporary build and project directories.
