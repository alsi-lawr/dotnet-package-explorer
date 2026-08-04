namespace Dotnet.PackageExplorer.Terminal.IntegrationTests

open System
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.Terminal
open Dotnet.PackageExplorer.Terminal.UnitTests.TestData
open FsUnit.Xunit
open Terminal.Gui.App
open Terminal.Gui.Input
open Xunit

[<Sealed>]
type InteractionTests() =
    let runAt width height initial keys =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(width, height))

        let messages = ResizeArray<Message>()

        use window =
            new ExplorerWindow(initial, messages.Add, application.RequestStop, Ansi16)

        use _keyboard = window.BindKeyboard application.Keyboard

        application.Invoke(
            Action(fun () ->
                keys
                |> List.iter (fun key -> application.Keyboard.RaiseKeyDownEvent key |> ignore)

                application.RequestStop())
        )

        application.Run window |> ignore
        messages |> Seq.toList

    let run initial keys = runAt 120 35 initial keys

    let runFailureTransitionAt width height initial openKeys scope afterKeys =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(width, height))

        let messages = ResizeArray<Message>()

        use window =
            new ExplorerWindow(initial, messages.Add, application.RequestStop, Ansi16)

        use _keyboard = window.BindKeyboard application.Keyboard

        let problem =
            { Scope = scope
              Kind = BackendUnavailable
              Message = "Workspace Explorer is unavailable." }

        let failed =
            let retained =
                match initial.Route with
                | Content route
                | Failure(route, _) -> route

            { initial with
                Route = Failure(retained, scope)
                Failures = Map.ofList [ scope, problem ] }

        application.Invoke(
            Action(fun () ->
                openKeys
                |> List.iter (fun key -> application.Keyboard.RaiseKeyDownEvent key |> ignore)

                window.Render failed

                afterKeys
                |> List.iter (fun key -> application.Keyboard.RaiseKeyDownEvent key |> ignore)

                application.RequestStop())
        )

        application.Run window |> ignore
        messages |> Seq.toList

    [<Fact>]
    member _.``public keyboard actions dispatch modes selection preview and refresh messages``() =
        let messages = run (model ()) [ Key.H.WithCtrl; Key.Space; Key.P; Key.R; Key.D1 ]

        messages |> should contain (ChangeMode Browse)
        messages |> should contain (SetPackageSelection(direct.Id, true))
        messages |> should contain (ShowTargeting direct.Id)
        messages |> should contain Refresh

    [<Fact>]
    member _.``public sort interaction reaches every field in both directions``() =
        let fields = [ Relevance; Name; Version; Type ]

        [ Ascending; Descending ]
        |> List.iter (fun direction ->
            fields
            |> List.iter (fun field ->
                let initial = model ()
                let start = fields |> List.findIndex ((=) initial.Sort.Field)
                let destination = fields |> List.findIndex ((=) field)
                let steps = (destination - start + fields.Length) % fields.Length

                let keys =
                    [ Key.S ]
                    @ List.replicate steps Key.L
                    @ (if direction = Descending then [ Key.J ] else [])
                    @ [ Key.Enter ]

                run initial keys
                |> should contain (ChangeSort { Field = field; Direction = direction })))

    [<Fact>]
    member _.``sort and its Help keep ownership until their local action closes``() =
        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let initial = model ()

            runAt
                width
                height
                initial
                [ Key.S
                  Key '?'
                  Key.D1
                  Key.J
                  Key.L
                  Key.Enter
                  Key.R
                  Key '?'
                  Key.Enter
                  Key.R ]
            |> should equal [ ChangeSort initial.Sort; Refresh ])

    [<Fact>]
    member _.``Help takes input ownership from an active search field``() =
        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            runAt
                width
                height
                (model ())
                [ Key '/'; Key '?'; Key.D1; Key.Enter; Key.R; Key '?'; Key.Esc; Key.R ]
            |> should equal [ Refresh ])

    [<Fact>]
    member _.``preview confirmation applies only after two public activations``() =
        let initial =
            { model () with
                Route = Content(OperationPreview(preview, Summary)) }

        run initial [ Key.L.WithCtrl; Key.Enter ]
        |> should not' (contain (ConfirmPreview preview.Id))

        run initial [ Key.L.WithCtrl; Key.Enter; Key.Enter ]
        |> should contain (ConfirmPreview preview.Id)

    [<Fact>]
    member _.``preview confirmation cancels through Escape without applying``() =
        let initial =
            { model () with
                Route = Content(OperationPreview(preview, Summary)) }

        run initial [ Key.L.WithCtrl; Key.Enter; Key.Esc ]
        |> should not' (contain (ConfirmPreview preview.Id))

    [<Fact>]
    member _.``targeting keys move to and select the next project``() =
        let initial =
            { model () with
                Route = Content(PackageTargeting direct.Id)
                Focus = ProjectRow(ProjectId "Web") }

        let messages = run initial [ Key.J; Key.L.WithCtrl; Key.Space; Key.P ]

        messages |> should contain (SetFocus(ProjectRow(ProjectId "Worker")))
        messages |> should contain (SetProjectSelection(ProjectId "Worker", true))

        messages
        |> should contain (RequestPreview(UpdateSelectedPackages(Set.singleton direct.Id)))

    [<Fact>]
    member _.``Escape cancels the visible pending request before dismissing content``() =
        let current = model ()

        let pending =
            { current with
                Pending.Refresh = Some(RequestToken 1L) }

        run pending [ Key.Esc ] |> should equal [ Cancel RefreshRequest ]

    [<Fact>]
    member _.``retained package rows do not dispatch package actions while list data is pending``
        ()
        =
        let browse =
            { model () with
                Mode = Browse
                Pending.Search = Some(RequestToken 40L) }

        let preview =
            { model () with
                Pending.Preview = Some(RequestToken 41L) }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            [ browse; preview ]
            |> List.iter (fun pending ->
                runAt
                    width
                    height
                    pending
                    [ Key.H.WithCtrl
                      Key.J
                      Key.Space
                      Key.Enter
                      Key.P
                      Key.S
                      Key.L
                      Key.Enter ]
                |> should be Empty))

    [<Fact>]
    member _.``operation routes block package commands and Help owns input until it closes``() =
        let progress =
            { Preview = preview.Id
              Operation = OperationId "operation-1"
              Completed = 1
              Total = 2
              Status = "Restoring projects" }

        let applying =
            { model () with
                Route = Content(OperationConfirmation preview)
                Pending.Apply = Some(RequestToken 20L) }

        let progressing =
            { applying with
                Route = Content(OperationProgress progress) }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            [ applying; progressing ]
            |> List.iter (fun operation ->
                runAt
                    width
                    height
                    operation
                    [ Key.Tab
                      Key.D1
                      Key.J
                      Key.L
                      Key.S
                      Key '/'
                      Key.Space
                      Key.Enter
                      Key.P
                      Key.R
                      Key.Esc
                      Key.Q
                      Key '?'
                      Key.D1
                      Key.Enter
                      Key.Esc
                      Key.R ]
                |> should be Empty))

    [<Fact>]
    member _.``Help over confirmation blocks apply and returns to the unchanged preview``() =
        let initial =
            { model () with
                Route = Content(OperationPreview(preview, Summary)) }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            runAt
                width
                height
                initial
                [ Key.L.WithCtrl
                  Key.Enter
                  Key.D1
                  Key.Space
                  Key.P
                  Key.R
                  Key '?'
                  Key.D1
                  Key.Enter
                  Key.Esc
                  Key.Enter ]
            |> should equal [ ConfirmPreview preview.Id ])

    [<Fact>]
    member _.``failure accepts only Help and its displayed dismiss action``() =
        let problem =
            { Scope = BackendSessionFailure
              Kind = BackendUnavailable
              Message = "Workspace Explorer is unavailable." }

        let failed =
            { model () with
                Route = Failure(PackageList, BackendSessionFailure)
                Failures = Map.ofList [ BackendSessionFailure, problem ] }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            runAt
                width
                height
                failed
                [ Key.D1; Key.R; Key '?'; Key.Enter; Key.D1; Key.Esc; Key.Esc ]
            |> should equal [ DismissFailure BackendSessionFailure ])

    [<Fact>]
    member _.``local source failures keep package navigation and contextual dismissal``() =
        let source = PackageSource "private-feed"
        let scope = SourceFailure source

        let problem =
            { Scope = scope
              Kind = AuthenticationRequired(Some source)
              Message = "Sign in to private-feed." }

        let failed =
            { model () with
                Route = Failure(PackageDetails direct.Id, scope)
                Pending = PendingRequests.empty
                Failures = Map.ofList [ scope, problem ] }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            runAt width height failed [ Key.L; Key.Esc ]
            |> should equal [ ShowReadme direct.Id; DismissFailure scope ])

    [<Fact>]
    member _.``external owned failure clears confirmation before accepting input``() =
        let confirming =
            { model () with
                Route = Content(OperationPreview(preview, Summary)) }

        [ BackendSessionFailure; OperationFailure(Some preview.Id) ]
        |> List.iter (fun scope ->
            [ 126, 34; 90, 30 ]
            |> List.iter (fun (width, height) ->
                runFailureTransitionAt
                    width
                    height
                    confirming
                    [ Key.L.WithCtrl; Key.Enter ]
                    scope
                    [ Key.D1; Key.R; Key.Enter; Key '?'; Key.D1; Key.Enter; Key.Esc; Key.Esc ]
                |> should equal [ DismissFailure scope ]))

    [<Fact>]
    member _.``external owned failure clears operation Help before accepting input``() =
        let applying =
            { model () with
                Route = Content(OperationConfirmation preview)
                Pending.Apply = Some(RequestToken 20L) }

        [ BackendSessionFailure; OperationFailure(Some preview.Id) ]
        |> List.iter (fun scope ->
            [ 126, 34; 90, 30 ]
            |> List.iter (fun (width, height) ->
                runFailureTransitionAt width height applying [ Key '?' ] scope [ Key.Esc ]
                |> should equal [ DismissFailure scope ]))
