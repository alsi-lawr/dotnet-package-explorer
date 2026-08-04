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
    let run initial keys =
        use application = Application.Create().Init "ansi"

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

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

        run initial [ Key.J ]
        |> should contain (SetFocus(ProjectRow(ProjectId "Worker")))

        let next =
            { initial with
                Focus = ProjectRow(ProjectId "Worker") }

        run next [ Key.H.WithCtrl; Key.Space ]
        |> should contain (SetProjectSelection(ProjectId "Worker", true))

    [<Fact>]
    member _.``Escape cancels the visible pending request before dismissing content``() =
        let current = model ()

        let pending =
            { current with
                Pending.Refresh = Some(RequestToken 1L) }

        run pending [ Key.Esc ] |> should equal [ Cancel RefreshRequest ]
