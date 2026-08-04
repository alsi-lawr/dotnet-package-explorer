namespace Dotnet.PackageExplorer.Terminal.IntegrationTests

open System
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open Dotnet.PackageExplorer.Terminal
open Dotnet.PackageExplorer.Terminal.UnitTests.TestData
open FsUnit.Xunit
open Terminal.Gui.App
open Terminal.Gui.Drivers
open Terminal.Gui.Input
open Xunit

[<RequireQualifiedAccess>]
module private Driver =
    let private required (driver: IDriver | null) =
        driver
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "The ANSI driver is unavailable.")

    let private cell (driver: IDriver | null) row column =
        let active = required driver

        let contents =
            active.Contents
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "The ANSI screen buffer is unavailable.")

        if contents.GetLength 0 = active.Rows then
            contents[row, column]
        else
            contents[column, row]

    let rows (driver: IDriver | null) =
        let active = required driver

        [ for row in 0 .. active.Rows - 1 do
              yield
                  [ for column in 0 .. active.Cols - 1 do
                        yield (cell driver row column).Grapheme ]
                  |> String.concat "" ]

    let text driver =
        rows driver |> String.concat Environment.NewLine

[<Sealed>]
type AnsiDriverTests() =
    let longPackage =
        package
            "Microsoft.Extensions.DependencyInjection"
            (Some Direct)
            (Some "1.2.3-alpha.1")
            (Some "10.0.0-rc.2")

    let renderWithKeys width height initial keys =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(width, height))

        application.StopAfterFirstIteration <- true
        use window = new ExplorerWindow(initial, ignore, application.RequestStop, Ansi16)
        use _keyboard = window.BindKeyboard application.Keyboard

        keys
        |> List.iter (fun key -> application.Keyboard.RaiseKeyDownEvent key |> ignore)

        application.Run window |> ignore
        Driver.text application.Driver

    let render width initial = renderWithKeys width 35 initial []

    [<Fact>]
    member _.``Terminal Gui v2 instance host renders one bounded ANSI driver iteration``() =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

        application.StopAfterFirstIteration <- true
        use window = new ExplorerWindow(model (), ignore, application.RequestStop, Ansi16)

        application.Run window |> ignore

        let screen = Driver.text application.Driver
        screen.Contains("Direct.Package", StringComparison.Ordinal) |> should equal true

        screen.Contains("Central.Package", StringComparison.Ordinal)
        |> should equal true

    [<Fact>]
    member _.``wide ANSI layout renders the package list beside its context``() =
        let details =
            { model () with
                Route = Content(PackageDetails direct.Id) }

        let wide = render 160 details
        wide.Contains("Central.Package", StringComparison.Ordinal) |> should equal true
        wide.Contains("Versions", StringComparison.Ordinal) |> should equal true

    [<Fact>]
    member _.``wide and compact lists show focus semantics and responsive version deltas``() =
        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let initial =
                { model () with
                    Mode = Updates
                    Packages = [ longPackage; central ]
                    ActivePackage = Some longPackage.Id
                    SelectedPackages = Set.singleton central.Id }

            let screen = renderWithKeys width height initial []
            screen.Contains("Current") |> should equal true
            screen.Contains("Latest") |> should equal true
            screen.Contains("Kind") |> should equal true
            screen.Contains("> [ ]") |> should equal true
            screen.Contains("  [x]") |> should equal true
            screen.Contains("1.2.3-alpha.1") |> should equal true
            screen.Contains("10.0.0-rc.2") |> should equal true

            if width = 90 then
                screen.Contains(longPackage.DisplayName) |> should equal true)

    [<Fact>]
    member _.``empty and loading lists render local recovery and stale-result notices``() =
        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let empty =
                { model () with
                    Mode = Browse
                    Query.Text = "missing"
                    Packages = []
                    ActivePackage = None }
                |> renderWithKeys width height
                <| []

            empty.Contains("No packages found.") |> should equal true
            empty.Contains("Press / to change") |> should equal true
            empty.Contains("Select a package") |> should equal false

            let loading =
                { model () with
                    Mode = Browse
                    Pending.Search = Some(RequestToken 40L) }
                |> renderWithKeys width height
                <| []

            loading.Contains("Searching packages...") |> should equal true
            loading.Contains("previous results") |> should equal true
            loading.Contains(">~") |> should equal true)

    [<Fact>]
    member _.``wide and compact operation states keep lifecycle actions and failures visible``() =
        let progress =
            { Preview = preview.Id
              Operation = OperationId "operation-1"
              Completed = 3
              Total = 4
              Status = "Restoring projects" }

        let failure =
            { Scope = BackendSessionFailure
              Kind = BackendUnavailable
              Message = "Workspace Explorer is unavailable. Check the installed tool." }

        [ 126, 34; 90, 30 ]
        |> List.iter (fun (width, height) ->
            let confirmation =
                { model () with
                    Route = Content(OperationPreview(preview, Summary)) }

            let confirmationScreen =
                renderWithKeys width height confirmation [ Key.L.WithCtrl; Key.Enter ]

            confirmationScreen.Contains("Apply package changes?") |> should equal true
            confirmationScreen.Contains("[Enter] Apply") |> should equal true
            confirmationScreen.Contains("[Esc] Cancel") |> should equal true
            confirmationScreen.Contains("[?] Help") |> should equal true

            let applyingState =
                { model () with
                    Route = Content(OperationConfirmation preview)
                    Pending.Apply = Some(RequestToken 20L) }

            let applying = renderWithKeys width height applyingState []

            applying.Contains("Applying changes for 2 packages.") |> should equal true
            applying.Contains("? Help") |> should equal true
            applying.Contains("Direct.Package") |> should equal false

            let help = renderWithKeys width height applyingState [ Key '?' ]
            help.Contains("Current actions") |> should equal true
            help.Contains("? / Esc") |> should equal true
            help.Contains("Close help") |> should equal true

            let progressing =
                { model () with
                    Route = Content(OperationProgress progress)
                    Pending.Apply = Some(RequestToken 20L) }
                |> renderWithKeys width height
                <| []

            progressing.Contains("Current step: Restoring projects") |> should equal true
            progressing.Contains("75%") |> should equal true
            progressing.Contains("Completed 3 of 4 steps.") |> should equal true

            let failed =
                { model () with
                    Route = Failure(PackageList, BackendSessionFailure)
                    Failures = Map.ofList [ BackendSessionFailure, failure ] }
                |> renderWithKeys width height
                <| []

            failed.Contains(failure.Message) |> should equal true
            failed.Contains("Press Esc to dismiss this message.") |> should equal true
            failed.Contains("Esc dismiss | ? Help") |> should equal true
            failed.Contains("Ready") |> should equal false
            failed.Contains("Direct.Package") |> should equal false)

    [<Fact>]
    member _.``live key events move focus and dispatch through the fixed window bindings``() =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

        application.StopAfterFirstIteration <- true
        let messages = ResizeArray<Message>()

        use window =
            new ExplorerWindow(model (), messages.Add, application.RequestStop, Ansi16)

        use _keyboard = window.BindKeyboard application.Keyboard

        application.Invoke(
            Action(fun () ->
                application.Keyboard.RaiseKeyDownEvent(Key '/') |> ignore

                let searchFocus =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> failwith "Search did not receive focus.")

                application.Keyboard.RaiseKeyDownEvent Key.L.WithCtrl |> ignore

                let contextFocus =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> failwith "Context did not receive focus.")

                Object.ReferenceEquals(searchFocus, contextFocus) |> should equal false

                application.Keyboard.RaiseKeyDownEvent Key.H.WithCtrl |> ignore

                let listFocus =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () ->
                        failwith "Package list did not receive focus.")

                Object.ReferenceEquals(contextFocus, listFocus) |> should equal false

                application.Keyboard.RaiseKeyDownEvent Key.D1 |> ignore
                application.Keyboard.RaiseKeyDownEvent Key.R |> ignore
                application.RequestStop())
        )

        application.Run window |> ignore

        messages |> Seq.toList |> should contain (ChangeMode Browse)
        messages |> Seq.toList |> should contain Refresh

    [<Fact>]
    member _.``Markdown viewport survives an in-place status refresh``() =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

        let longReadme =
            [ 1..80 ]
            |> List.map (fun line -> $"## Section {line}\n\nContent for section {line}.")
            |> String.concat "\n\n"

        let initial =
            { model () with
                Route = Content(PackageReadme direct.Id)
                Readmes =
                    Map.ofList
                        [ direct.Id,
                          { Package = direct.Id
                            CommonMark = longReadme } ] }

        use window = new ExplorerWindow(initial, ignore, application.RequestStop, Ansi16)
        use _keyboard = window.BindKeyboard application.Keyboard

        application.Invoke(
            Action(fun () ->
                application.Keyboard.RaiseKeyDownEvent Key.L.WithCtrl |> ignore

                let context =
                    window.MostFocused
                    |> Option.ofObj
                    |> Option.defaultWith (fun () -> failwith "Context did not receive focus.")

                context.ScrollVertical 4 |> should equal true
                let scrolled = context.Viewport.Y
                scrolled |> should be (greaterThan 0)

                window.Render
                    { initial with
                        Pending.Refresh = Some(RequestToken 88L) }

                context.Viewport.Y |> should equal scrolled
                application.RequestStop())
        )

        application.Run window |> ignore

[<Sealed>]
type ConnectedRuntimeTests() =
    [<Fact>]
    member _.``connected composition closes the client and subscription once after an ANSI run``() =
        let mutable closeCount = 0
        let mutable disposeCount = 0

        let subscription =
            { new IDisposable with
                member _.Dispose() = disposeCount <- disposeCount + 1 }

        let packageClient =
            client (fun () -> async { closeCount <- closeCount + 1 }) (fun _ -> subscription)

        let connection =
            { Client = packageClient
              Capabilities = Set.empty
              ServerCapabilities = Set.empty }

        let createApplication () =
            let application = Application.Create()
            application.StopAfterFirstIteration <- true
            application

        Runtime.runConnected
            createApplication
            "ansi"
            Ansi16
            (Runtime.target "App.fsproj")
            connection
        |> should equal 0

        closeCount |> should equal 1
        disposeCount |> should equal 1
