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
    member _.``background status and failures remain visible without replacing package rows``() =
        let initial = model ()

        let problem =
            { Scope = BackendSessionFailure
              Kind = BackendUnavailable
              Message = "Workspace Explorer is unavailable." }

        let actual =
            { initial with
                Route = Failure(PackageList, BackendSessionFailure)
                Failures = Map.ofList [ BackendSessionFailure, problem ] }
            |> Presentation.project 160

        actual.Rows |> should haveLength 2
        actual.Route |> should equal "Failure"
        actual.Context.Contains(problem.Message) |> should equal true
