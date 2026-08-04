namespace Dotnet.PackageExplorer.Terminal.UnitTests

open System
open Dotnet.PackageExplorer.Application

module TestData =
    let project name frameworks =
        { Id = ProjectId name
          Name = name
          Frameworks = frameworks |> List.map TargetFramework }

    let target =
        Solution(
            "Example.slnx",
            [ project "Web" [ "net10.0"; "net9.0" ]; project "Worker" [ "net10.0" ] ]
        )

    let package id kind current latest =
        { Id = PackageId id
          DisplayName = id
          InstalledVersion = current |> Option.map PackageVersion
          LatestVersion = latest |> Option.map PackageVersion
          Kind = kind
          Source = Some(PackageSource "nuget.org")
          Relevance = Some 1.0
          Description = Some($"{id} package") }

    let direct = package "Direct.Package" (Some Direct) (Some "1.0.0") (Some "2.0.0")
    let central = package "Central.Package" (Some Central) (Some "1.0.0") (Some "2.0.0")

    let snapshot =
        { Items =
            [ direct; central ]
            |> List.map (fun package ->
                { Target =
                    { Project = ProjectId "Web"
                      Framework = Some(TargetFramework "net10.0")
                      Runtime = None }
                  Package = package })
          CapturedAt = DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero) }

    let model () =
        Model.create target Model.allCapabilities (Some snapshot)
        |> fst
        |> fun model ->
            { model with
                ActivePackage = Some direct.Id
                Details =
                    Map.ofList
                        [ direct.Id,
                          { Package = direct
                            Versions = [ PackageVersion "2.0.0"; PackageVersion "1.0.0" ]
                            Dependencies =
                              [ { Id = PackageId "Dependency"
                                  VersionRange = "[1.0.0,)" } ]
                            IsDeprecated = false
                            Vulnerabilities = []
                            License = Some "MIT" } ]
                Readmes =
                    Map.ofList
                        [ direct.Id,
                          { Package = direct.Id
                            CommonMark = "# Direct.Package\n\nREADME body." } ] }

    let update message model = Update.update message model |> fst

    let preview =
        { Id = PreviewId "preview-1"
          Operation = UpdateSelectedPackages(Set.ofList [ direct.Id; central.Id ])
          Summary = [ "Update two packages." ]
          Projects =
            [ { Project = ProjectId "Web"
                Framework = Some(TargetFramework "net10.0")
                Before = Some(PackageVersion "1.0.0")
                After = Some(PackageVersion "2.0.0") } ]
          Dependencies =
            [ { Id = PackageId "Dependency"
                VersionRange = "[2.0.0,)" } ]
          Files = [ "src/Web/Web.fsproj" ] }
