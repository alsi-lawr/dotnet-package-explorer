namespace Dotnet.PackageExplorer.Application.UnitTests

open Dotnet.PackageExplorer.Application
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type ModeEntryTests() =
    let scroll =
        { PackageOffset = 5
          DetailsOffset = 4
          ProjectOffset = 3
          PreviewOffset = 2 }

    [<Fact>]
    member _.``entering Browse starts a current search and ignores a replaced mode request``() =
        let source = PackageSource "private-feed"

        let prepared =
            model solution (Some(snapshot [ directPackage ]))
            |> updateModel (ChangeSearch("json", true))
            |> updateModel (ChangePage 2)
            |> updateModel (SelectSource(Some source))
            |> updateModel (SetFocus PackageSearch)
            |> updateModel (SetScroll scroll)

        let first, firstEffects = update (ChangeMode Browse) prepared
        let firstToken = requestToken firstEffects

        match firstEffects with
        | [ SearchPackages request ] ->
            request.Target |> should equal solution
            request.Source |> should equal (Some source)
            request.Query |> should equal prepared.Query
            first.Pending.Search |> should equal (Some request.Token)
        | _ -> failwith "Expected one Browse mode package search."

        let current, currentEffects = update (ChangeMode Browse) first
        let currentToken = requestToken currentEffects

        let stale =
            current
            |> updateModel (
                SearchCompleted(
                    firstToken,
                    Ok
                        { Query = first.Query
                          Packages = [ directPackage ]
                          HasNextPage = true }
                )
            )

        stale |> should equal current

        let actual =
            stale
            |> updateModel (
                SearchCompleted(
                    currentToken,
                    Ok
                        { Query = current.Query
                          Packages = [ browsePackage ]
                          HasNextPage = false }
                )
            )

        actual.Packages |> should equal [ browsePackage ]
        actual.Pending.Search |> should equal None
        actual.Focus |> should equal current.Focus
        actual.Scroll |> should equal current.Scroll
        actual.Installed |> should equal current.Installed

    [<Fact>]
    member _.``entering Installed shows cached rows while a current refresh remains pending``() =
        let installed = snapshot [ directPackage ]

        let first, firstEffects =
            update (ChangeMode Installed) (model solution (Some installed))

        let firstToken = requestToken firstEffects

        first.Mode |> should equal Installed
        first.Installed |> should equal (Some installed)
        first.Packages |> should equal [ directPackage ]

        match firstEffects with
        | [ RefreshInstalled request ] ->
            request.Target |> should equal solution
            first.Pending.Refresh |> should equal (Some request.Token)
        | _ -> failwith "Expected one Installed mode refresh."

        let current, currentEffects = update (ChangeMode Installed) first
        let currentToken = requestToken currentEffects

        let contextual =
            current
            |> updateModel (SetPackageSelection(directPackage.Id, true))
            |> updateModel (SetFocus(PackageRow directPackage.Id))
            |> updateModel (SetScroll scroll)

        let stale =
            contextual
            |> updateModel (RefreshCompleted(firstToken, Ok(snapshot [ transitivePackage ])))

        stale |> should equal contextual

        let refreshed = snapshot [ centralPackage ]

        let actual = stale |> updateModel (RefreshCompleted(currentToken, Ok refreshed))

        actual.Installed |> should equal (Some refreshed)
        actual.Packages |> should equal [ centralPackage ]
        actual.Pending.Refresh |> should equal None
        actual.SelectedPackages |> should equal contextual.SelectedPackages
        actual.Focus |> should equal contextual.Focus
        actual.Scroll |> should equal contextual.Scroll

    [<Fact>]
    member _.``mode-entry failure and cancellation retain immediate Installed rows``() =
        let installed = snapshot [ directPackage ]
        let initial = model solution (Some installed)
        let waiting, failingEffects = update (ChangeMode Installed) initial
        let failingToken = requestToken failingEffects

        let failing =
            waiting
            |> updateModel (SetPackageSelection(directPackage.Id, true))
            |> updateModel (SetFocus(PackageRow directPackage.Id))
            |> updateModel (SetScroll scroll)

        let failed =
            failing
            |> updateModel (
                RefreshCompleted(
                    failingToken,
                    Error(failure BackendSessionFailure BackendUnavailable "Offline.")
                )
            )

        failed.Installed |> should equal (Some installed)
        failed.Packages |> should equal [ directPackage ]
        failed.SelectedPackages |> should equal failing.SelectedPackages
        failed.Focus |> should equal failing.Focus
        failed.Scroll |> should equal failing.Scroll

        let cancellationWaiting, _ = update (ChangeMode Installed) failed

        let cancelling =
            cancellationWaiting
            |> updateModel (SetPackageSelection(directPackage.Id, true))
            |> updateModel (SetFocus(PackageRow directPackage.Id))
            |> updateModel (SetScroll scroll)

        let cancelled, cancellationEffects = update (Cancel RefreshRequest) cancelling

        cancelled.Installed |> should equal (Some installed)
        cancelled.Packages |> should equal [ directPackage ]
        cancelled.SelectedPackages |> should equal cancelling.SelectedPackages
        cancelled.Focus |> should equal cancelling.Focus
        cancelled.Scroll |> should equal cancelling.Scroll
        cancelled.Failures[BackendSessionFailure].Kind |> should equal Cancelled

        match cancellationEffects with
        | [ CancelRequest _ ] -> ()
        | _ -> failwith "Expected one Installed mode cancellation."
