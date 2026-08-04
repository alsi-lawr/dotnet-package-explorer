namespace Dotnet.PackageExplorer.Application.UnitTests

open Dotnet.PackageExplorer.Application
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type OperationsAndFailuresTests() =
    [<Fact>]
    member _.``multi-package updates share one preview identity and expose every preview tab``() =
        let selected = Set.ofList [ directPackage.Id; centralPackage.Id ]
        let operation = UpdateSelectedPackages selected
        let initial = model solution (Some(snapshot [ directPackage; centralPackage ]))
        let waiting, effects = update (RequestPreview operation) initial
        let token = requestToken effects
        let expectedPreview = preview "preview-1" operation
        let shown = waiting |> updateModel (PreviewCompleted(token, Ok expectedPreview))

        [ Summary; Projects; Dependencies; Files ]
        |> List.iter (fun tab ->
            let actual = shown |> updateModel (SelectPreviewTab tab)
            actual.Route |> should equal (Content(OperationPreview(expectedPreview, tab))))

        match effects with
        | [ PreviewOperation request ] ->
            request.Operation |> should equal operation
            request.Selection |> should equal initial.TargetSelection
        | _ -> failwith "Expected one preview request."

    [<Fact>]
    member _.``a replaced preview and stale confirmation cannot apply an older operation``() =
        let firstOperation = UpdateSelectedPackages(Set.singleton directPackage.Id)
        let secondOperation = UninstallPackage centralPackage.Id
        let initial = model solution None
        let first, firstEffects = update (RequestPreview firstOperation) initial
        let firstToken = requestToken firstEffects
        let second, secondEffects = update (RequestPreview secondOperation) first
        let secondToken = requestToken secondEffects
        let oldPreview = preview "old" firstOperation
        let currentPreview = preview "current" secondOperation

        let shown =
            second
            |> updateModel (PreviewCompleted(firstToken, Ok oldPreview))
            |> updateModel (PreviewCompleted(secondToken, Ok currentPreview))

        let unchanged, effects = update (ConfirmPreview oldPreview.Id) shown

        unchanged |> should equal shown
        effects |> should be Empty

        let confirming, applyEffects = update (ConfirmPreview currentPreview.Id) shown

        match applyEffects with
        | [ ApplyOperation request ] ->
            request.Preview |> should equal currentPreview.Id
            confirming.Route |> should equal (Content(OperationConfirmation currentPreview))
        | _ -> failwith "Expected one apply request."

    [<Fact>]
    member _.``current apply progress completes with the backend snapshot``() =
        let operation = UpdateSelectedPackages(Set.singleton directPackage.Id)
        let expectedPreview = preview "preview-1" operation
        let initial = model solution (Some(snapshot [ directPackage ]))
        let waitingPreview, previewEffects = update (RequestPreview operation) initial
        let previewToken = requestToken previewEffects

        let shown =
            waitingPreview
            |> updateModel (PreviewCompleted(previewToken, Ok expectedPreview))

        let applying, applyEffects = update (ConfirmPreview expectedPreview.Id) shown
        let applyToken = requestToken applyEffects

        let progress =
            { Preview = expectedPreview.Id
              Operation = OperationId "operation-1"
              Completed = 1
              Total = 2
              Status = "Restoring" }

        let progressed = applying |> updateModel (ApplyProgressed(applyToken, progress))
        progressed.Route |> should equal (Content(OperationProgress progress))

        let updated =
            snapshot
                [ { directPackage with
                      InstalledVersion = Some(PackageVersion "2.0.0") } ]

        let result =
            { Preview = expectedPreview.Id
              Installed = updated
              Summary = "Updated" }

        let actual = progressed |> updateModel (ApplyCompleted(applyToken, Ok result))
        actual.Installed |> should equal (Some updated)
        actual.Route |> should equal (Content PackageList)

    [<Fact>]
    member _.``a superseded apply response cannot replace the current operation state``() =
        let firstOperation = UpdateSelectedPackages(Set.singleton directPackage.Id)
        let secondOperation = UninstallPackage centralPackage.Id
        let initial = model solution (Some(snapshot [ directPackage; centralPackage ]))

        let firstWaiting, firstPreviewEffects =
            update (RequestPreview firstOperation) initial

        let firstPreviewToken = requestToken firstPreviewEffects
        let firstPreview = preview "first" firstOperation

        let firstShown =
            firstWaiting
            |> updateModel (PreviewCompleted(firstPreviewToken, Ok firstPreview))

        let firstApplying, firstApplyEffects =
            update (ConfirmPreview firstPreview.Id) firstShown

        let firstApplyToken = requestToken firstApplyEffects

        let secondWaiting, secondPreviewEffects =
            update (RequestPreview secondOperation) firstApplying

        let secondPreviewToken = requestToken secondPreviewEffects
        let secondPreview = preview "second" secondOperation

        let secondShown =
            secondWaiting
            |> updateModel (PreviewCompleted(secondPreviewToken, Ok secondPreview))

        let secondApplying, secondApplyEffects =
            update (ConfirmPreview secondPreview.Id) secondShown

        let secondApplyToken = requestToken secondApplyEffects

        let staleResult =
            { Preview = firstPreview.Id
              Installed = snapshot [ centralPackage ]
              Summary = "Stale" }

        let stale =
            secondApplying |> updateModel (ApplyCompleted(firstApplyToken, Ok staleResult))

        stale |> should equal secondApplying

        let currentResult =
            { Preview = secondPreview.Id
              Installed = snapshot [ directPackage ]
              Summary = "Current" }

        let actual =
            stale |> updateModel (ApplyCompleted(secondApplyToken, Ok currentResult))

        actual.Installed |> should equal (Some currentResult.Installed)

    [<Fact>]
    member _.``authentication failure stays on its source and retains package context``() =
        let initial = model solution None
        let loading, effects = update (ShowDetails directPackage.Id) initial
        let token = requestToken effects
        let source = PackageSource "private-feed"

        let authFailure =
            failure
                (SourceFailure source)
                (AuthenticationRequired(Some source))
                "Sign in to private-feed."

        let actual =
            loading
            |> updateModel (DetailsCompleted(token, directPackage.Id, Error authFailure))

        actual.Route
        |> should equal (Failure(PackageDetails directPackage.Id, SourceFailure source))

        actual.Failures[SourceFailure source] |> should equal authFailure
        actual.Mode |> should equal Installed

    [<Fact>]
    member _.``backend exit keeps selection sorting focus paging scroll and the visible route``() =
        let sort =
            { Field = Version
              Direction = Descending }

        let scroll =
            { PackageOffset = 5
              DetailsOffset = 4
              ProjectOffset = 3
              PreviewOffset = 2 }

        let beforeFailure =
            model solution (Some(snapshot [ directPackage ]))
            |> updateModel (ChangeSort sort)
            |> updateModel (ChangeSearch("json", true))
            |> updateModel (ChangePage 2)
            |> updateModel (SelectPackage directPackage.Id)
            |> updateModel (SetPackageSelection(directPackage.Id, true))
            |> updateModel (ShowTargeting directPackage.Id)
            |> updateModel (SetFocus DetailsPane)
            |> updateModel (SetScroll scroll)

        let exited =
            failure BackendSessionFailure (BackendExited(Some 17)) "Workspace Explorer exited."

        let actual = beforeFailure |> updateModel (BackendSessionFailed exited)

        actual.Route
        |> should equal (Failure(PackageTargeting directPackage.Id, BackendSessionFailure))

        actual.Mode |> should equal beforeFailure.Mode
        actual.ActivePackage |> should equal (Some directPackage.Id)
        actual.SelectedPackages |> should equal beforeFailure.SelectedPackages
        actual.Sort |> should equal sort
        actual.Query |> should equal beforeFailure.Query
        actual.Focus |> should equal DetailsPane
        actual.Scroll |> should equal scroll

    [<Fact>]
    member _.``recovery clears only the affected failure and preserves unrelated failures``() =
        let source = PackageSource "private-feed"

        let sourceFailure =
            failure (SourceFailure source) (AuthenticationRequired(Some source)) "Sign in."

        let backendFailure =
            failure BackendSessionFailure BackendUnavailable "Disconnected."

        let loading, detailsEffects =
            model solution (Some(snapshot [ directPackage ]))
            |> update (ShowDetails directPackage.Id)

        let detailsToken = requestToken detailsEffects

        let failed =
            loading
            |> updateModel (DetailsCompleted(detailsToken, directPackage.Id, Error sourceFailure))
            |> updateModel (BackendSessionFailed backendFailure)

        let refreshing, effects = update Refresh failed
        let token = requestToken effects

        let actual =
            refreshing
            |> updateModel (RefreshCompleted(token, Ok(snapshot [ centralPackage ])))

        actual.Failures.ContainsKey BackendSessionFailure |> should equal false
        actual.Failures[SourceFailure source] |> should equal sourceFailure
        actual.Packages |> should equal [ centralPackage ]

    [<Fact>]
    member _.``small capability sets keep available state and reject unsupported routes``() =
        let capabilities = Set.singleton ReadInstalledPackages

        let initial, _ =
            Model.create solution capabilities (Some(snapshot [ directPackage ]))

        let unsupported = initial |> updateModel (ChangeMode Browse)

        unsupported.Mode |> should equal Installed
        unsupported.Packages |> should equal [ directPackage ]
        unsupported.Route |> should equal (Failure(PackageList, BackendSessionFailure))

        let detailsUnavailable = unsupported |> updateModel (ShowDetails directPackage.Id)

        detailsUnavailable.Failures[PackageFailure directPackage.Id].Kind
        |> should equal (Rejected "The backend does not support ReadPackageDetails.")
