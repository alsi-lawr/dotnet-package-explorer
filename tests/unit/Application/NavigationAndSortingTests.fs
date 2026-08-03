namespace Dotnet.PackageExplorer.Application.UnitTests

open Dotnet.PackageExplorer.Application
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type NavigationAndSortingTests() =
    [<Fact>]
    member _.``all four package modes use explicit state and their expected default sort``() =
        let initial = model solution (Some(snapshot [ directPackage; transitivePackage ]))

        [ Browse, Relevance, Descending
          Installed, Name, Ascending
          Updates, Name, Ascending
          Consolidate, Name, Ascending ]
        |> List.iter (fun (mode, field, direction) ->
            let actual = updateModel (ChangeMode mode) initial
            actual.Mode |> should equal mode
            actual.Sort |> should equal { Field = field; Direction = direction }
            actual.Route |> should equal (Content PackageList))

    [<Fact>]
    member _.``installed sorting puts direct packages before the selected field``() =
        let equalNameDirect =
            { directPackage with
                DisplayName = "Same" }

        let equalNameCentral =
            { centralPackage with
                DisplayName = "Same" }

        let actual =
            model solution (Some(snapshot [ equalNameCentral; transitivePackage; equalNameDirect ]))
            |> updateModel (ChangeSort { Field = Name; Direction = Ascending })

        actual.Packages
        |> List.map _.Id
        |> should equal [ equalNameDirect.Id; transitivePackage.Id; equalNameCentral.Id ]

    [<Fact>]
    member _.``browse relevance sorting keeps missing relevance after scored packages``() =
        let unscored =
            { browsePackage with
                Id = PackageId "Unscored"
                Relevance = None }

        let weaker =
            { browsePackage with
                Id = PackageId "Weaker"
                Relevance = Some 0.2 }

        let stronger =
            { browsePackage with
                Id = PackageId "Stronger"
                Relevance = Some 0.9 }

        let browse = model solution None |> updateModel (ChangeMode Browse)
        let searching, effects = update SubmitSearch browse
        let token = requestToken effects

        let actual =
            searching
            |> updateModel (
                SearchCompleted(
                    token,
                    Ok
                        { Query = searching.Query
                          Packages = [ unscored; weaker; stronger ]
                          HasNextPage = false }
                )
            )

        actual.Packages
        |> List.map _.Id
        |> should equal [ stronger.Id; weaker.Id; unscored.Id ]

    [<Fact>]
    member _.``solution and workspace targets expose project and framework selection``() =
        let app = ProjectId "App"
        let framework = TargetFramework "net10.0"
        let workspace = Workspace("Directory.Build.props", [ project "App" [ "net10.0" ] ])

        [ solution; workspace ]
        |> List.iter (fun target ->
            let actual =
                model target None
                |> updateModel (ShowTargeting directPackage.Id)
                |> updateModel (SetProjectSelection(app, true))
                |> updateModel (SetFrameworkSelection(app, framework, true))

            actual.Route |> should equal (Content(PackageTargeting directPackage.Id))
            actual.TargetSelection.Projects.Contains app |> should equal true
            actual.TargetSelection.Frameworks[app].Contains framework |> should equal true)

    [<Fact>]
    member _.``single project targeting stays implicit and cannot be edited``() =
        let initial = model singleProject None
        let selectedProject = ProjectId "App"

        let actual =
            initial
            |> updateModel (ShowTargeting directPackage.Id)
            |> updateModel (SetProjectSelection(selectedProject, false))

        actual.Route |> should equal (Content PackageList)
        actual.TargetSelection.Projects |> should equal (Set.singleton selectedProject)

        actual.TargetSelection.Frameworks[selectedProject]
        |> should equal (Set.singleton (TargetFramework "net10.0"))

    [<Fact>]
    member _.``search input preserves paging choices in the emitted request``() =
        let initial = model solution None |> updateModel (ChangeMode Browse)

        let searching, effects =
            initial
            |> updateModel (ChangeSearch("json", true))
            |> updateModel (ChangePage 3)
            |> update SubmitSearch

        match effects with
        | [ SearchPackages request ] ->
            request.Query.Text |> should equal "json"
            request.Query.IncludePrerelease |> should equal true
            request.Query.Page |> should equal 3
            searching.Pending.Search |> should equal (Some request.Token)
        | _ -> failwith "Expected one package search request."

    [<Fact>]
    member _.``selected package source is retained and projected into search requests``() =
        let source = PackageSource "private-feed"

        let searching, effects =
            model solution None
            |> updateModel (ChangeMode Browse)
            |> updateModel (SelectSource(Some source))
            |> update SubmitSearch

        searching.SelectedSource |> should equal (Some source)

        match effects with
        | [ SearchPackages request ] -> request.Source |> should equal (Some source)
        | _ -> failwith "Expected one source-specific package search."

    [<Fact>]
    member _.``selected package version is retained and projected into operation previews``() =
        let version = PackageVersion "3.0.0"

        let selected =
            model solution None
            |> updateModel (SelectVersion(directPackage.Id, Some version))

        selected.SelectedVersions[directPackage.Id] |> should equal version

        [ InstallPackage(directPackage.Id, None), InstallPackage(directPackage.Id, Some version)
          ConsolidatePackage(directPackage.Id, PackageVersion "caller"),
          ConsolidatePackage(directPackage.Id, version) ]
        |> List.iter (fun (operation, expected) ->
            let _, effects = update (RequestPreview operation) selected

            match effects with
            | [ PreviewOperation request ] -> request.Operation |> should equal expected
            | _ -> failwith "Expected one version-specific operation preview.")
