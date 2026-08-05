namespace Dotnet.PackageExplorer.Terminal.UnitTests

open System
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.Terminal
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type PresentationTests() =
    let longPackage =
        package
            "Microsoft.Extensions.DependencyInjection"
            (Some Direct)
            (Some "1.2.3-alpha.1")
            (Some "10.0.0-rc.2")

    let dependencyInjection =
        package
            "Microsoft.Extensions.DependencyInjection"
            (Some Direct)
            (Some "8.0.0")
            (Some "9.0.0")

    let solutionPersistence =
        package
            "Microsoft.VisualStudio.SolutionPersistence"
            (Some Central)
            (Some "1.0.0")
            (Some "1.1.0")

    [<Fact>]
    member _.``each package mode projects a distinct non-flat package hierarchy``() =
        let initial = model ()

        [ Browse, "Packages |"
          Installed, "Installed packages"
          Updates, "Available updates"
          Consolidate, "Version differences" ]
        |> List.iter (fun (mode, expectedTitle) ->
            let actual = initial |> update (ChangeMode mode) |> Presentation.project 160

            actual.ListTitle |> should startWith expectedTitle
            actual.Modes |> should contain $"[{mode}]"
            actual.ContextTitle |> should not' (be EmptyString))

    [<Fact>]
    member _.``installed projections keep direct packages first and label package state``() =
        let actual = model () |> Presentation.project 160

        (actual.Rows.Head).Contains("Direct.Package") |> should equal true
        (actual.Rows.Head).Contains("Direct") |> should equal true
        (actual.Rows[1]).Contains("Central") |> should equal true
        (actual.ListTitle).Contains("Sort:") |> should equal true
        actual.ListHeading.Contains("Package") |> should equal true
        actual.ListHeading.Contains("Version") |> should equal true
        actual.ListHeading.Contains("Kind") |> should equal true

    [<Fact>]
    member _.``wide and compact package rows use their available width for package and version meaning``
        ()
        =
        [ Browse, [ "Version"; "Source" ]
          Installed, [ "Version"; "Kind" ]
          Updates, [ "Current"; "Latest"; "Kind" ]
          Consolidate, [ "Current"; "Latest"; "Kind" ] ]
        |> List.iter (fun (mode, headings) ->
            [ 126, 81; 90, 86 ]
            |> List.iter (fun (columns, maximumRowWidth) ->
                let actual =
                    { model () with
                        Mode = mode
                        Packages = [ longPackage ]
                        ActivePackage = Some longPackage.Id }
                    |> Presentation.project columns

                headings
                |> List.iter (fun heading ->
                    actual.ListHeading.Contains heading |> should equal true)

                actual.Rows.Head.Length |> should be (lessThanOrEqualTo maximumRowWidth)

                if columns = 90 then
                    actual.Rows.Head.Contains(longPackage.DisplayName) |> should equal true

                match mode with
                | Updates
                | Consolidate ->
                    actual.Rows.Head.Contains("1.2.3-alpha.1") |> should equal true
                    actual.Rows.Head.Contains("10.0.0-rc.2") |> should equal true
                | Browse
                | Installed -> ()))

    [<Fact>]
    member _.``wide and compact package lists retain complete audited package identities``() =
        [ Browse, [ "Version"; "Source" ]
          Installed, [ "Version"; "Kind" ]
          Updates, [ "Current"; "Latest"; "Kind" ]
          Consolidate, [ "Current"; "Latest"; "Kind" ] ]
        |> List.iter (fun (mode, headings) ->
            [ 126; 90 ]
            |> List.iter (fun columns ->
                let actual =
                    { model () with
                        Mode = mode
                        Packages = [ dependencyInjection; solutionPersistence ]
                        ActivePackage = Some dependencyInjection.Id }
                    |> Presentation.project columns

                headings
                |> List.iter (fun heading ->
                    actual.ListHeading.Contains heading |> should equal true)

                [ dependencyInjection; solutionPersistence ]
                |> List.iter (fun package ->
                    actual.Rows
                    |> List.exists (_.Contains(package.DisplayName))
                    |> should equal true)))

    [<Fact>]
    member _.``package focus remains separate from relationship color and multi-selection``() =
        let actual =
            { model () with
                Mode = Updates
                ActivePackage = Some direct.Id
                SelectedPackages = Set.singleton central.Id }
            |> Presentation.project 90

        actual.Rows.Head.StartsWith("> [ ]") |> should equal true
        actual.Rows.Head.Contains("Direct") |> should equal true
        actual.Rows[1].StartsWith("  [x]") |> should equal true
        actual.Rows[1].Contains("Central") |> should equal true

    [<Fact>]
    member _.``unloaded package details only report loading while their request is active``() =
        let unloaded = { model () with Details = Map.empty }

        let selected = Presentation.project 126 unloaded
        selected.Context |> should equal "Press Enter to load package details."

        let loading =
            { unloaded with
                Route = Content(PackageDetails direct.Id)
                Pending.Details = Some(RequestToken 40L) }
            |> Presentation.project 126

        loading.Context |> should equal "Loading package details..."

    [<Fact>]
    member _.``empty package modes expose only their available recovery actions``() =
        let emptyModes =
            [ Browse, "Tab/1-4 modes | / search | ? Help", "No packages found"
              Installed, "1 Browse | r refresh | ? Help", "No installed packages"
              Updates, "Tab/1-4 modes | / filters | r refresh | ? Help", "No updates found"
              Consolidate,
              "Tab/1-4 modes | / filters | r refresh | ? Help",
              "No version differences found" ]

        [ 126; 90 ]
        |> List.iter (fun columns ->
            emptyModes
            |> List.iter (fun (mode, expectedActions, expectedNotice) ->
                let actual =
                    { model () with
                        Mode = mode
                        Query.Text = "missing"
                        Packages = []
                        ActivePackage = None }
                    |> Presentation.project columns

                actual.ListIsInteractive |> should equal false
                actual.ListNotice.Value.Contains(expectedNotice) |> should equal true
                actual.Context.Contains("Select a package") |> should equal false
                actual.ListTitle.Contains("Sort: Package, ascending") |> should equal true
                actual.ListTitle.Contains("[s]") |> should equal false
                actual.Actions |> should equal expectedActions))

    [<Fact>]
    member _.``retained loading package modes expose cancellation without package actions``() =
        let pending =
            [ { model () with
                  Mode = Browse
                  Pending.Search = Some(RequestToken 40L) },
              "Searching packages"
              { model () with
                  Pending.Preview = Some(RequestToken 41L) },
              "Building preview"
              { model () with
                  Pending.Refresh = Some(RequestToken 42L) },
              "Refreshing installed packages"
              { model () with
                  Mode = Updates
                  Pending.Updates = Some(RequestToken 43L) },
              "Finding package updates"
              { model () with
                  Mode = Consolidate
                  Pending.Consolidation = Some(RequestToken 44L) },
              "Finding version differences" ]

        [ 126; 90 ]
        |> List.iter (fun columns ->
            pending
            |> List.iter (fun (initial, expectedNotice) ->
                let actual = Presentation.project columns initial

                actual.ListIsInteractive |> should equal false
                actual.ListNotice.Value.Contains(expectedNotice) |> should equal true

                actual.ListNotice.Value.Contains("package actions are paused")
                |> should equal true

                actual.ListTitle.Contains("Sort: Package, ascending") |> should equal true
                actual.ListTitle.Contains("[s]") |> should equal false
                actual.Rows.Head.StartsWith(">~") |> should equal true
                actual.Rows[1].StartsWith(" ~") |> should equal true
                actual.Rows |> List.exists (_.StartsWith("~~")) |> should equal false
                actual.Actions |> should equal "Tab/1-4 modes | Esc cancel | ? Help"))

    [<Fact>]
    member _.``interactive list titles expose the active sort field direction and shortcut``() =
        [ Browse; Installed; Updates; Consolidate ]
        |> List.iter (fun mode ->
            [ 126; 90 ]
            |> List.iter (fun columns ->
                let actual =
                    { model () with
                        Mode = mode
                        Sort = { Field = Name; Direction = Descending } }
                    |> Presentation.project columns

                actual.ListIsInteractive |> should equal true
                actual.ListTitle.Contains("Sort: Package, descending [s]") |> should equal true))

    [<Fact>]
    member _.``wide previews preserve the package list and expose every preview tab``() =
        [ Summary; Projects; Dependencies; Files ]
        |> List.iter (fun tab ->
            let previewModel =
                { model () with
                    Route = Content(OperationPreview(preview, tab)) }

            let actual = Presentation.project 160 previewModel

            actual.Width |> should equal Wide
            actual.Rows |> should haveLength 2
            actual.Route |> should startWith "Packages /"
            actual.Context |> should not' (be EmptyString))

    [<Fact>]
    member _.``narrow details route uses the backend CommonMark README without rewriting it``() =
        let initial = model ()

        let actual =
            { initial with
                Route = Content(PackageReadme direct.Id) }
            |> Presentation.project 80

        actual.Width |> should equal Narrow
        actual.ContextTitle |> should equal "Workspace Explorer / Packages"
        actual.Route |> should equal "Packages / README"
        actual.Context |> should equal "# Direct.Package\n\nREADME body."

    [<Fact>]
    member _.``targets show project parents framework children and complete local instructions``() =
        [ 126; 90 ]
        |> List.iter (fun columns ->
            let initial =
                { model () with
                    Route = Content(PackageTargeting direct.Id)
                    Focus = ProjectRow(ProjectId "Web")
                    TargetSelection =
                        { Projects = Set.singleton (ProjectId "Web")
                          Frameworks =
                            Map.ofList
                                [ ProjectId "Web",
                                  Set.ofList
                                      [ TargetFramework "net10.0"; TargetFramework "net9.0" ] ] } }

            let actual = Presentation.project columns initial

            actual.ContextTitle |> should equal "Workspace Explorer / Packages"
            actual.Route |> should equal "Packages / Targets"
            actual.Context.Contains("Web") |> should equal true
            actual.Context.Contains("net10.0") |> should equal true
            actual.Context.Contains("net9.0") |> should equal true
            actual.Context.Contains("Worker") |> should equal true
            actual.Context.Contains("j/k move") |> should equal true

            actual.Context.Contains("Space select project and all frameworks")
            |> should equal true

            actual.Context.Contains("p preview") |> should equal true)

    [<Fact>]
    member _.``details and previews preserve literal ranges and distinguish impacted files``() =
        [ 126; 90 ]
        |> List.iter (fun columns ->
            let details =
                { model () with
                    Route = Content(PackageDetails direct.Id) }
                |> Presentation.project columns

            details.ContextTitle |> should equal "Workspace Explorer / Packages"
            details.Route |> should equal "Packages / Details"
            details.Context.Contains("# Direct.Package") |> should equal false

            let dependencies =
                { model () with
                    Route = Content(OperationPreview(preview, Dependencies)) }
                |> Presentation.project columns

            dependencies.ContextTitle |> should equal "Workspace Explorer / Packages"
            dependencies.Route |> should equal "Packages / Dependencies"
            dependencies.Context.Contains("# Dependency impact") |> should equal false

            let files =
                { model () with
                    Route = Content(OperationPreview(preview, Files)) }
                |> Presentation.project columns

            files.Route |> should equal "Packages / Files"
            files.Context.Contains("- Changed: src/Web/Web.fsproj") |> should equal true
            files.Context.Split("Changed:").Length - 1 |> should equal preview.Files.Length)

    [<Fact>]
    member _.``operation routes expose safe actions and observable apply progress``() =
        let progress =
            { Preview = preview.Id
              Operation = OperationId "operation-1"
              Completed = 3
              Total = 4
              Status = "Restoring projects" }

        [ 126; 90 ]
        |> List.iter (fun columns ->
            let applying =
                { model () with
                    Route = Content(OperationConfirmation preview)
                    Pending.Apply = Some(RequestToken 20L) }
                |> Presentation.project columns

            applying.ContextTitle |> should equal "Applying package changes"
            applying.Context.Contains("2 packages") |> should equal true
            applying.Actions |> should equal "? Help"
            applying.Route |> should equal "Packages / Applying"

            let progressing =
                { model () with
                    Route = Content(OperationProgress progress)
                    Pending.Apply = Some(RequestToken 20L) }
                |> Presentation.project columns

            progressing.Context.Contains("Current step: Restoring projects")
            |> should equal true

            progressing.Context.Contains("3 of 4 steps") |> should equal true
            progressing.Context.Contains("75%") |> should equal true
            progressing.Context.Contains("[###############.....]") |> should equal true
            progressing.Actions |> should equal "? Help")

    [<Fact>]
    member _.``application failures replace package actions with the complete recovery message``() =
        let initial = model ()

        let problem =
            { Scope = BackendSessionFailure
              Kind = BackendUnavailable
              Message = "Workspace Explorer is unavailable." }

        [ 126; 90 ]
        |> List.iter (fun columns ->
            let actual =
                { initial with
                    Route = Failure(PackageList, BackendSessionFailure)
                    Failures = Map.ofList [ BackendSessionFailure, problem ] }
                |> Presentation.project columns

            actual.Rows |> should haveLength 2
            actual.Route |> should equal "Packages / Failure"
            actual.Context.Contains(problem.Message) |> should equal true
            actual.Context.Contains("Press Esc to dismiss") |> should equal true
            actual.Actions |> should equal "Esc dismiss | ? Help"
            actual.Status.Contains("Ready") |> should equal false)

    [<Fact>]
    member _.``local failures retain package context without owning the whole explorer``() =
        let source = PackageSource "private-feed"

        let problem =
            { Scope = SourceFailure source
              Kind = AuthenticationRequired(Some source)
              Message = "Sign in to private-feed." }

        [ 126; 90 ]
        |> List.iter (fun columns ->
            let actualModel =
                { model () with
                    Route = Failure(PackageDetails direct.Id, SourceFailure source)
                    Failures = Map.ofList [ SourceFailure source, problem ] }

            let actual = Presentation.project columns actualModel

            Presentation.ownsInput actualModel |> should equal false
            actual.Rows |> should haveLength 2
            actual.ContextTitle |> should equal "Package Explorer"
            actual.Context |> should equal problem.Message
            actual.Route |> should equal "Failure"
            actual.Actions.Contains("Esc dismiss") |> should equal true
            actual.Actions.Contains("? Help") |> should equal true
            actual.Actions.Contains(Environment.NewLine) |> should equal false)

    [<Fact>]
    member _.``every route supplies one contextual guidance row with Help discoverable``() =
        let progress =
            { Preview = preview.Id
              Operation = OperationId "operation-1"
              Completed = 1
              Total = 2
              Status = "Restoring projects" }

        let routes =
            [ Content PackageList
              Content(PackageDetails direct.Id)
              Content(PackageReadme direct.Id)
              Content(PackageTargeting direct.Id)
              Content(OperationPreview(preview, Summary))
              Content(OperationConfirmation preview)
              Content(OperationProgress progress) ]

        routes
        |> List.iter (fun route ->
            let actual = { model () with Route = route } |> Presentation.project 90

            actual.Actions.Contains("? Help") |> should equal true
            actual.Actions.Contains(Environment.NewLine) |> should equal false)
