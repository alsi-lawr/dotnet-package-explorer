namespace Dotnet.PackageExplorer.Application.UnitTests

open Dotnet.PackageExplorer.Application
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type ModeDiscoveryTests() =
    let target =
        { Project = ProjectId "App"
          Framework = Some(TargetFramework "net10.0")
          Runtime = None }

    let updatePage package continuation =
        { Updates =
            [ { Package = package
                Target = target
                InstalledVersion = Some(PackageVersion "1.0.0")
                AvailableVersions = [ PackageVersion "3.0.0"; PackageVersion "2.0.0" ] } ]
          Continuation = continuation }

    let consolidationPage package continuation =
        { Packages =
            [ { Package = package
                CurrentVersions = [ PackageVersion "1.0.0", [ target ] ]
                CandidateVersions = [ PackageVersion "3.0.0"; PackageVersion "2.0.0" ] } ]
          Continuation = continuation }

    let withPackageFailure package state =
        let loading, effects = update (ShowDetails package) state
        let token = requestToken effects

        loading
        |> updateModel (
            DetailsCompleted(
                token,
                package,
                Error(
                    failure
                        (PackageFailure package)
                        (Rejected "details_unavailable")
                        "Details are unavailable."
                )
            )
        )

    [<Fact>]
    member _.``Updates requests backend rows and current results preserve interaction context``() =
        let initial =
            model solution (Some(snapshot [ directPackage ]))
            |> updateModel (ChangeSearch("", true))

        let waiting, effects = update (ChangeMode Updates) initial
        let token = requestToken effects

        match effects with
        | [ FindPackageUpdates request ] ->
            request.Target |> should equal solution
            request.IncludePrerelease |> should equal true
            request.PageSize |> should equal initial.Query.PageSize
            request.Continuation |> should equal None
            waiting.Pending.Updates |> should equal (Some request.Token)
        | _ -> failwith "Expected one package updates request."

        let contextual =
            waiting
            |> updateModel (SetPackageSelection(directPackage.Id, true))
            |> updateModel (
                ChangeSort
                    { Field = Version
                      Direction = Descending }
            )
            |> updateModel (SetFocus(PackageRow directPackage.Id))

        let loadingDetails, detailsEffects =
            update (ShowDetails directPackage.Id) contextual

        let detailsToken = requestToken detailsEffects

        let packageFailure =
            failure
                (PackageFailure directPackage.Id)
                (Rejected "details_unavailable")
                "Details are unavailable."

        let withFailure =
            loadingDetails
            |> updateModel (DetailsCompleted(detailsToken, directPackage.Id, Error packageFailure))

        let page = updatePage directPackage.Id (Some "next")
        let actual = withFailure |> updateModel (UpdatesCompleted(token, Ok page))

        actual.AvailableUpdates |> should equal (Some page)
        actual.Pending.Updates |> should equal None
        actual.HasNextPage |> should equal true
        actual.Packages |> should haveLength 1
        actual.Packages.Head.Id |> should equal directPackage.Id

        actual.Packages.Head.InstalledVersion
        |> should equal (Some(PackageVersion "1.0.0"))

        actual.Packages.Head.LatestVersion
        |> should equal (Some(PackageVersion "3.0.0"))

        actual.Packages.Head.Kind |> should equal (Some Direct)
        actual.SelectedPackages |> should equal withFailure.SelectedPackages
        actual.Sort |> should equal withFailure.Sort
        actual.Focus |> should equal withFailure.Focus
        actual.Failures |> should equal withFailure.Failures
        actual.Route |> should equal withFailure.Route

    [<Fact>]
    member _.``Consolidate requests backend rows and maps a current result into mode state``() =
        let initial = model solution (Some(snapshot [ centralPackage ]))
        let waiting, effects = update (ChangeMode Consolidate) initial
        let token = requestToken effects

        match effects with
        | [ FindPackageConsolidation request ] ->
            request.Target |> should equal solution
            request.PageSize |> should equal initial.Query.PageSize
            request.Continuation |> should equal None
            waiting.Pending.Consolidation |> should equal (Some request.Token)
        | _ -> failwith "Expected one package consolidation request."

        let page = consolidationPage centralPackage.Id None
        let actual = waiting |> updateModel (ConsolidationCompleted(token, Ok page))

        actual.AvailableConsolidation |> should equal (Some page)
        actual.Pending.Consolidation |> should equal None
        actual.HasNextPage |> should equal false
        actual.Packages |> should haveLength 1
        actual.Packages.Head.Id |> should equal centralPackage.Id

        actual.Packages.Head.InstalledVersion
        |> should equal (Some(PackageVersion "1.0.0"))

        actual.Packages.Head.LatestVersion
        |> should equal (Some(PackageVersion "3.0.0"))

        actual.Packages.Head.Kind |> should equal (Some Central)

    [<Fact>]
    member _.``stale Updates and Consolidate results cannot replace current mode state``() =
        let initial = model solution (Some(snapshot [ directPackage; centralPackage ]))
        let firstUpdates, firstUpdateEffects = update (ChangeMode Updates) initial
        let firstUpdateToken = requestToken firstUpdateEffects
        let currentUpdates, currentUpdateEffects = update SubmitSearch firstUpdates
        let currentUpdateToken = requestToken currentUpdateEffects

        let currentUpdates =
            currentUpdates
            |> updateModel (SetPackageSelection(directPackage.Id, true))
            |> updateModel (
                ChangeSort
                    { Field = Version
                      Direction = Descending }
            )
            |> updateModel (SetFocus(PackageRow directPackage.Id))
            |> withPackageFailure directPackage.Id

        let staleUpdates =
            currentUpdates
            |> updateModel (
                UpdatesCompleted(firstUpdateToken, Ok(updatePage directPackage.Id (Some "stale")))
            )

        staleUpdates |> should equal currentUpdates

        let acceptedUpdates =
            staleUpdates
            |> updateModel (
                UpdatesCompleted(currentUpdateToken, Ok(updatePage directPackage.Id None))
            )

        acceptedUpdates.SelectedPackages |> should equal currentUpdates.SelectedPackages
        acceptedUpdates.Sort |> should equal currentUpdates.Sort
        acceptedUpdates.Focus |> should equal currentUpdates.Focus
        acceptedUpdates.Failures |> should equal currentUpdates.Failures
        acceptedUpdates.Route |> should equal currentUpdates.Route

        let firstConsolidation, firstConsolidationEffects =
            update (ChangeMode Consolidate) initial

        let firstConsolidationToken = requestToken firstConsolidationEffects

        let currentConsolidation, currentConsolidationEffects =
            update SubmitSearch firstConsolidation

        let currentConsolidationToken = requestToken currentConsolidationEffects

        let currentConsolidation =
            currentConsolidation
            |> updateModel (SetPackageSelection(centralPackage.Id, true))
            |> updateModel (ChangeSort { Field = Type; Direction = Ascending })
            |> updateModel (SetFocus(PackageRow centralPackage.Id))
            |> withPackageFailure centralPackage.Id

        let staleConsolidation =
            currentConsolidation
            |> updateModel (
                ConsolidationCompleted(
                    firstConsolidationToken,
                    Ok(consolidationPage centralPackage.Id (Some "stale"))
                )
            )

        staleConsolidation |> should equal currentConsolidation

        let acceptedConsolidation =
            staleConsolidation
            |> updateModel (
                ConsolidationCompleted(
                    currentConsolidationToken,
                    Ok(consolidationPage centralPackage.Id None)
                )
            )

        acceptedConsolidation.SelectedPackages
        |> should equal currentConsolidation.SelectedPackages

        acceptedConsolidation.Focus |> should equal currentConsolidation.Focus
        acceptedConsolidation.Sort |> should equal currentConsolidation.Sort
        acceptedConsolidation.Failures |> should equal currentConsolidation.Failures
        acceptedConsolidation.Route |> should equal currentConsolidation.Route

    [<Fact>]
    member _.``Updates and Consolidate failure and cancellation retain visible mode rows``() =
        let backendFailure =
            failure BackendSessionFailure BackendUnavailable "Backend unavailable."

        let initial = model solution (Some(snapshot [ directPackage; centralPackage ]))
        let updateWaiting, updateEffects = update (ChangeMode Updates) initial
        let updateToken = requestToken updateEffects

        let loadedUpdates =
            updateWaiting
            |> updateModel (
                UpdatesCompleted(updateToken, Ok(updatePage directPackage.Id (Some "next")))
            )
            |> updateModel (SetPackageSelection(directPackage.Id, true))
            |> updateModel (SetFocus(PackageRow directPackage.Id))

        let failingUpdates, failingUpdateEffects = update SubmitSearch loadedUpdates
        let failingUpdateToken = requestToken failingUpdateEffects

        let failedUpdates =
            failingUpdates
            |> updateModel (UpdatesCompleted(failingUpdateToken, Error backendFailure))

        failedUpdates.Packages |> should equal loadedUpdates.Packages
        failedUpdates.AvailableUpdates |> should equal loadedUpdates.AvailableUpdates
        failedUpdates.SelectedPackages |> should equal loadedUpdates.SelectedPackages
        failedUpdates.Focus |> should equal loadedUpdates.Focus

        let cancellingUpdates, _ = update SubmitSearch loadedUpdates

        let cancelledUpdates, updateCancelEffects =
            update (Cancel UpdatesRequest) cancellingUpdates

        cancelledUpdates.Packages |> should equal loadedUpdates.Packages
        cancelledUpdates.AvailableUpdates |> should equal loadedUpdates.AvailableUpdates
        cancelledUpdates.SelectedPackages |> should equal loadedUpdates.SelectedPackages
        cancelledUpdates.Sort |> should equal loadedUpdates.Sort
        cancelledUpdates.Focus |> should equal loadedUpdates.Focus

        match updateCancelEffects with
        | [ CancelRequest _ ] -> ()
        | _ -> failwith "Expected one updates cancellation request."

        let consolidationWaiting, consolidationEffects =
            update (ChangeMode Consolidate) initial

        let consolidationToken = requestToken consolidationEffects

        let loadedConsolidation =
            consolidationWaiting
            |> updateModel (
                ConsolidationCompleted(
                    consolidationToken,
                    Ok(consolidationPage centralPackage.Id (Some "next"))
                )
            )
            |> updateModel (SetPackageSelection(centralPackage.Id, true))
            |> updateModel (SetFocus(PackageRow centralPackage.Id))

        let failingConsolidation, failingConsolidationEffects =
            update SubmitSearch loadedConsolidation

        let failingConsolidationToken = requestToken failingConsolidationEffects

        let failedConsolidation =
            failingConsolidation
            |> updateModel (ConsolidationCompleted(failingConsolidationToken, Error backendFailure))

        failedConsolidation.Packages |> should equal loadedConsolidation.Packages

        failedConsolidation.AvailableConsolidation
        |> should equal loadedConsolidation.AvailableConsolidation

        failedConsolidation.SelectedPackages
        |> should equal loadedConsolidation.SelectedPackages

        failedConsolidation.Focus |> should equal loadedConsolidation.Focus

        let cancellingConsolidation, _ = update SubmitSearch loadedConsolidation

        let cancelledConsolidation, consolidationCancelEffects =
            update (Cancel ConsolidationRequest) cancellingConsolidation

        cancelledConsolidation.Packages |> should equal loadedConsolidation.Packages

        cancelledConsolidation.AvailableConsolidation
        |> should equal loadedConsolidation.AvailableConsolidation

        cancelledConsolidation.SelectedPackages
        |> should equal loadedConsolidation.SelectedPackages

        cancelledConsolidation.Sort |> should equal loadedConsolidation.Sort
        cancelledConsolidation.Focus |> should equal loadedConsolidation.Focus

        match consolidationCancelEffects with
        | [ CancelRequest _ ] -> ()
        | _ -> failwith "Expected one consolidation cancellation request."
