namespace Dotnet.PackageExplorer.Application.UnitTests

open Dotnet.PackageExplorer.Application
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type AsynchronousUpdateTests() =
    let pagedBrowseModel () =
        let initial = model solution None |> updateModel (ChangeMode Browse)
        let searching, effects = update SubmitSearch initial
        let token = requestToken effects

        searching
        |> updateModel (
            SearchCompleted(
                token,
                Ok
                    { Query = searching.Query
                      Packages = [ browsePackage ]
                      HasNextPage = true }
            )
        )

    [<Fact>]
    member _.``a rapid replacement search ignores the superseded response``() =
        let browse = model solution None |> updateModel (ChangeMode Browse)

        let first, firstEffects =
            browse |> updateModel (ChangeSearch("old", false)) |> update SubmitSearch

        let firstToken = requestToken firstEffects

        let second, secondEffects =
            first |> updateModel (ChangeSearch("new", false)) |> update SubmitSearch

        let secondToken = requestToken secondEffects

        let stale =
            second
            |> updateModel (
                SearchCompleted(
                    firstToken,
                    Ok
                        { Query = first.Query
                          Packages = [ directPackage ]
                          HasNextPage = false }
                )
            )

        stale |> should equal second

        let current =
            stale
            |> updateModel (
                SearchCompleted(
                    secondToken,
                    Ok
                        { Query = second.Query
                          Packages = [ browsePackage ]
                          HasNextPage = false }
                )
            )

        current.Query.Text |> should equal "new"
        current.Packages |> should equal [ browsePackage ]

    [<Fact>]
    member _.``current search responses retain true and false next-page availability``() =
        let initial = model solution None |> updateModel (ChangeMode Browse)
        let first, firstEffects = update SubmitSearch initial
        let firstToken = requestToken firstEffects

        let withNextPage =
            first
            |> updateModel (
                SearchCompleted(
                    firstToken,
                    Ok
                        { Query = first.Query
                          Packages = [ browsePackage ]
                          HasNextPage = true }
                )
            )

        withNextPage.HasNextPage |> should equal true

        let second, secondEffects = update SubmitSearch withNextPage
        let secondToken = requestToken secondEffects
        second.HasNextPage |> should equal false

        let withoutNextPage =
            second
            |> updateModel (
                SearchCompleted(
                    secondToken,
                    Ok
                        { Query = second.Query
                          Packages = [ directPackage ]
                          HasNextPage = false }
                )
            )

        withoutNextPage.HasNextPage |> should equal false

    [<Fact>]
    member _.``replacement failure cancellation and backend exit reset paging availability``() =
        let paged = pagedBrowseModel ()
        let replaced = paged |> updateModel (ChangeSearch("replacement", false))
        replaced.HasNextPage |> should equal false

        let failing, failureEffects = update SubmitSearch paged
        let failureToken = requestToken failureEffects

        let failed =
            failing
            |> updateModel (
                SearchCompleted(
                    failureToken,
                    Error(failure BackendSessionFailure BackendUnavailable "Offline")
                )
            )

        failed.HasNextPage |> should equal false

        let cancelling, _ = update SubmitSearch paged
        let cancelled = cancelling |> updateModel (Cancel SearchRequest)
        cancelled.HasNextPage |> should equal false

        let fatal = failure BackendSessionFailure (BackendExited(Some 17)) "Backend exited."
        let exited = paged |> updateModel (BackendSessionFailed fatal)
        exited.HasNextPage |> should equal false

    [<Fact>]
    member _.``stale page response preserves the current next-page availability``() =
        let initial = model solution None |> updateModel (ChangeMode Browse)
        let first, firstEffects = update SubmitSearch initial
        let firstToken = requestToken firstEffects
        let second, secondEffects = update SubmitSearch first
        let secondToken = requestToken secondEffects

        let current =
            second
            |> updateModel (
                SearchCompleted(
                    secondToken,
                    Ok
                        { Query = second.Query
                          Packages = [ browsePackage ]
                          HasNextPage = true }
                )
            )

        let actual =
            current
            |> updateModel (
                SearchCompleted(
                    firstToken,
                    Ok
                        { Query = first.Query
                          Packages = [ directPackage ]
                          HasNextPage = false }
                )
            )

        actual |> should equal current
        actual.HasNextPage |> should equal true

    [<Fact>]
    member _.``superseded details and README responses cannot replace the current package``() =
        let initial = model solution None
        let oldPackage = directPackage.Id
        let currentPackage = transitivePackage.Id
        let firstDetails, firstDetailsEffects = update (ShowDetails oldPackage) initial
        let oldDetailsToken = requestToken firstDetailsEffects

        let currentDetails, currentDetailsEffects =
            update (ShowDetails currentPackage) firstDetails

        let currentDetailsToken = requestToken currentDetailsEffects

        let staleDetails =
            currentDetails
            |> updateModel (
                DetailsCompleted(oldDetailsToken, oldPackage, Ok(details directPackage))
            )

        staleDetails |> should equal currentDetails

        let shownDetails =
            staleDetails
            |> updateModel (
                DetailsCompleted(currentDetailsToken, currentPackage, Ok(details transitivePackage))
            )

        shownDetails.Route |> should equal (Content(PackageDetails currentPackage))
        shownDetails.Details.ContainsKey currentPackage |> should equal true

        let firstReadme, firstReadmeEffects = update (ShowReadme oldPackage) shownDetails
        let oldReadmeToken = requestToken firstReadmeEffects

        let currentReadme, currentReadmeEffects =
            update (ShowReadme currentPackage) firstReadme

        let currentReadmeToken = requestToken currentReadmeEffects

        let oldReadme =
            { Package = oldPackage
              CommonMark = "old" }

        let currentReadmeValue =
            { Package = currentPackage
              CommonMark = "current" }

        let actual =
            currentReadme
            |> updateModel (ReadmeCompleted(oldReadmeToken, oldPackage, Ok oldReadme))
            |> updateModel (
                ReadmeCompleted(currentReadmeToken, currentPackage, Ok currentReadmeValue)
            )

        actual.Readmes.ContainsKey oldPackage |> should equal false
        actual.Readmes[currentPackage] |> should equal currentReadmeValue
        actual.Route |> should equal (Content(PackageReadme currentPackage))

    [<Fact>]
    member _.``the installed snapshot is visible while its background refresh remains pending``() =
        let installed = snapshot [ directPackage ]
        let actual, effects = Model.create solution Model.allCapabilities (Some installed)

        actual.Installed |> should equal (Some installed)
        actual.Packages |> should equal [ directPackage ]

        match effects with
        | [ RefreshInstalled request ] ->
            actual.Pending.Refresh |> should equal (Some request.Token)
        | _ -> failwith "Expected one background installed refresh."

    [<Fact>]
    member _.``a current refresh updates package data without changing interaction context``() =
        let initial = model solution (Some(snapshot [ directPackage ]))

        let scroll =
            { PackageOffset = 4
              DetailsOffset = 2
              ProjectOffset = 1
              PreviewOffset = 3 }

        let refreshing, effects =
            initial
            |> updateModel (ChangeMode Updates)
            |> updateModel (SetPackageSelection(directPackage.Id, true))
            |> updateModel (SetFocus(PackageRow directPackage.Id))
            |> updateModel (SetScroll scroll)
            |> update Refresh

        let token = requestToken effects
        let refreshed = snapshot [ directPackage; centralPackage ]
        let actual = refreshing |> updateModel (RefreshCompleted(token, Ok refreshed))

        actual.Mode |> should equal Updates
        actual.SelectedPackages |> should equal (Set.singleton directPackage.Id)
        actual.Focus |> should equal (PackageRow directPackage.Id)
        actual.Scroll |> should equal scroll
        actual.Installed |> should equal (Some refreshed)

    [<Fact>]
    member _.``a superseded refresh cannot replace the current installed snapshot``() =
        let initial = model solution (Some(snapshot [ directPackage ]))
        let first, firstEffects = update Refresh initial
        let firstToken = requestToken firstEffects
        let second, secondEffects = update Refresh first
        let secondToken = requestToken secondEffects
        let staleSnapshot = snapshot [ transitivePackage ]
        let currentSnapshot = snapshot [ centralPackage ]

        let stale = second |> updateModel (RefreshCompleted(firstToken, Ok staleSnapshot))

        stale |> should equal second

        let actual =
            stale |> updateModel (RefreshCompleted(secondToken, Ok currentSnapshot))

        actual.Installed |> should equal (Some currentSnapshot)
        actual.Packages |> should equal [ centralPackage ]

    [<Fact>]
    member _.``refresh failure and cancellation retain the visible installed data``() =
        let installed = snapshot [ directPackage ]
        let initial = model solution (Some installed)
        let refreshing, effects = update Refresh initial
        let token = requestToken effects

        let failed =
            refreshing
            |> updateModel (
                RefreshCompleted(
                    token,
                    Error(failure BackendSessionFailure BackendUnavailable "offline")
                )
            )

        failed.Installed |> should equal (Some installed)
        failed.Packages |> should equal [ directPackage ]

        let refreshingAgain, _ = update Refresh failed
        let cancelled, cancelEffects = update (Cancel RefreshRequest) refreshingAgain

        cancelled.Installed |> should equal (Some installed)
        cancelled.Failures[BackendSessionFailure].Kind |> should equal Cancelled

        match cancelEffects with
        | [ CancelRequest _ ] -> ()
        | _ -> failwith "Expected one cancellation request."

    [<Fact>]
    member _.``backend exit invalidates every pending response and retains the fatal failure``() =
        let initial = model solution (Some(snapshot [ directPackage ]))
        let fatal = failure BackendSessionFailure (BackendExited(Some 17)) "Backend exited."

        let searching, searchEffects =
            initial |> updateModel (ChangeMode Browse) |> update SubmitSearch

        let searchToken = requestToken searchEffects

        let searchResponse =
            SearchCompleted(
                searchToken,
                Ok
                    { Query = searching.Query
                      Packages = [ browsePackage ]
                      HasNextPage = false }
            )

        let refreshing, refreshEffects = update Refresh initial
        let refreshToken = requestToken refreshEffects

        let refreshResponse =
            RefreshCompleted(refreshToken, Ok(snapshot [ centralPackage ]))

        let updatesLoading, updatesEffects = update (ChangeMode Updates) initial
        let updatesToken = requestToken updatesEffects

        let updatesResponse =
            UpdatesCompleted(updatesToken, Ok { Updates = []; Continuation = None })

        let consolidationLoading, consolidationEffects =
            update (ChangeMode Consolidate) initial

        let consolidationToken = requestToken consolidationEffects

        let consolidationResponse =
            ConsolidationCompleted(consolidationToken, Ok { Packages = []; Continuation = None })

        let detailsLoading, detailsEffects = update (ShowDetails directPackage.Id) initial
        let detailsToken = requestToken detailsEffects

        let detailsResponse =
            DetailsCompleted(detailsToken, directPackage.Id, Ok(details directPackage))

        let readmeLoading, readmeEffects = update (ShowReadme directPackage.Id) initial
        let readmeToken = requestToken readmeEffects

        let readmeResponse =
            ReadmeCompleted(
                readmeToken,
                directPackage.Id,
                Ok
                    { Package = directPackage.Id
                      CommonMark = "# Late" }
            )

        let operation = UpdateSelectedPackages(Set.singleton directPackage.Id)
        let previewLoading, previewEffects = update (RequestPreview operation) initial
        let previewToken = requestToken previewEffects
        let operationPreview = preview "late-preview" operation
        let previewResponse = PreviewCompleted(previewToken, Ok operationPreview)

        let previewShown =
            previewLoading
            |> updateModel (PreviewCompleted(previewToken, Ok operationPreview))

        let applying, applyEffects =
            update (ConfirmPreview operationPreview.Id) previewShown

        let applyToken = requestToken applyEffects

        let applyResponse =
            ApplyCompleted(
                applyToken,
                Ok
                    { Preview = operationPreview.Id
                      Installed = snapshot [ centralPackage ]
                      Summary = "Late" }
            )

        [ searching, searchResponse
          refreshing, refreshResponse
          updatesLoading, updatesResponse
          consolidationLoading, consolidationResponse
          detailsLoading, detailsResponse
          readmeLoading, readmeResponse
          previewLoading, previewResponse
          applying, applyResponse ]
        |> List.iter (fun (pending, lateResponse) ->
            let failed = pending |> updateModel (BackendSessionFailed fatal)
            let actual = failed |> updateModel lateResponse

            failed.Pending |> should equal PendingRequests.empty

            match failed.Route with
            | Failure(_, scope) -> scope |> should equal BackendSessionFailure
            | Content _ -> failwith "Expected the fatal backend failure route."

            actual |> should equal failed)
