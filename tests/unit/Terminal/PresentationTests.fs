namespace Dotnet.PackageExplorer.Terminal.UnitTests

open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.Terminal
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type PresentationTests() =
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
            actual.Route |> should startWith "Preview /"
            actual.Context |> should not' (be EmptyString))

    [<Fact>]
    member _.``narrow details route uses the backend CommonMark README without rewriting it``() =
        let initial = model ()

        let actual =
            { initial with
                Route = Content(PackageReadme direct.Id) }
            |> Presentation.project 80

        actual.Width |> should equal Narrow
        actual.Route |> should equal "README"
        actual.Context |> should equal "# Direct.Package\n\nREADME body."

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
            actual.Actions.Contains("1-4 jump") |> should equal true)
