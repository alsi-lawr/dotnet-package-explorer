namespace Dotnet.PackageExplorer.Application.UnitTests

open System
open Dotnet.PackageExplorer.Application

module TestData =
    let project name frameworks =
        { Id = ProjectId name
          Name = name
          Frameworks = frameworks |> List.map TargetFramework }

    let singleProject = SingleProject(project "App" [ "net10.0" ])

    let solution =
        Solution("Example.slnx", [ project "App" [ "net10.0" ]; project "Lib" [ "net9.0" ] ])

    let package id name kind relevance =
        { Id = PackageId id
          DisplayName = name
          InstalledVersion = Some(PackageVersion "1.0.0")
          LatestVersion = Some(PackageVersion "2.0.0")
          Kind = kind
          Source = Some(PackageSource "nuget.org")
          Relevance = relevance
          Description = Some $"{name} description" }

    let directPackage = package "Direct.Package" "Zulu" (Some Direct) (Some 0.2)

    let transitivePackage =
        package "Transitive.Package" "Alpha" (Some Transitive) (Some 0.9)

    let centralPackage = package "Central.Package" "Beta" (Some Central) (Some 0.5)
    let browsePackage = package "Browse.Package" "Browse" None (Some 1.0)

    let snapshot packages =
        { Packages = packages
          CapturedAt = DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.Zero) }

    let model target installed =
        Model.create target Model.allCapabilities installed |> fst

    let update message model = Update.update message model

    let updateModel message model = update message model |> fst

    let requestToken effects =
        match effects with
        | [ SearchPackages request ] -> request.Token
        | [ RefreshInstalled request ] -> request.Token
        | [ GetPackageDetails request ] -> request.Token
        | [ GetPackageReadme request ] -> request.Token
        | [ PreviewOperation request ] -> request.Token
        | [ ApplyOperation request ] -> request.Token
        | _ -> failwith $"Expected one request effect, received {effects}."

    let preview id operation =
        { Id = PreviewId id
          Operation = operation
          Summary = [ "Summary" ]
          Projects =
            [ { Project = ProjectId "App"
                Framework = Some(TargetFramework "net10.0")
                Before = Some(PackageVersion "1.0.0")
                After = Some(PackageVersion "2.0.0") } ]
          Dependencies =
            [ { Id = PackageId "Dependency"
                VersionRange = "[2.0.0,)" } ]
          Files = [ "App.fsproj" ] }

    let details packageSummary =
        { Package = packageSummary
          Versions = [ PackageVersion "2.0.0" ]
          Dependencies = []
          IsDeprecated = false
          Vulnerabilities = []
          License = Some "MIT" }

    let failure scope kind message =
        { Scope = scope
          Kind = kind
          Message = message }
