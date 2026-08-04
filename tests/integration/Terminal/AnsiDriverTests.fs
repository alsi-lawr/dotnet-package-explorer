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
    let render width initial =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(width, 35))

        application.StopAfterFirstIteration <- true
        use window = new ExplorerWindow(initial, ignore, application.RequestStop, Ansi16)
        application.Run window |> ignore
        Driver.text application.Driver

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
