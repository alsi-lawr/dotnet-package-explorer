#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
workspace="$(mktemp -d "${TMPDIR:-/tmp}/dotnet-package-explorer-showcase.XXXXXX")"
build="$(mktemp -d "${TMPDIR:-/tmp}/dotnet-package-explorer-vhs.XXXXXX")"
trap 'rm -rf "$workspace" "$build"' EXIT

cat > "$workspace/Showcase.fsproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Humanizer.Core" Version="2.8.26" />
    <PackageReference Include="Newtonsoft.Json" Version="12.0.1" />
    <PackageReference Include="Serilog" Version="2.10.0" />
  </ItemGroup>
</Project>
PROJECT

printf 'printfn "Package Explorer showcase"\n' > "$workspace/Program.fs"
dotnet restore "$workspace/Showcase.fsproj" >/dev/null

vhs_bin="${VHS_BIN:-}"

if [[ -z "$vhs_bin" ]]; then
  vhs_source="${VHS_SOURCE:-/home/alex/dev/vhs}"
  GOBIN="$build" go -C "$vhs_source" install ./...
  vhs_bin="$build/vhs"
fi

export PACKAGE_EXPLORER_SHOWCASE_TARGET="$workspace/Showcase.fsproj"
cd "$root"
"$vhs_bin" showcase/package-explorer.tape
