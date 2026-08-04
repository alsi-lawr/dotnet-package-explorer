namespace Dotnet.PackageExplorer.Terminal.UnitTests

open System
open System.Drawing
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.Terminal
open FsUnit.Xunit
open Terminal.Gui.App
open Terminal.Gui.Drawing
open Terminal.Gui.Input
open Terminal.Gui.ViewBase
open Terminal.Gui.Views
open TestData
open Xunit

[<Sealed>]
type RendererTests() =
    [<Fact>]
    member _.``Terminal Gui v2 instance host renders one bounded ANSI driver iteration``() =
        use application = Application.Create().Init("ansi")

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

        application.StopAfterFirstIteration <- true
        use window = new ExplorerWindow(model (), ignore, application.RequestStop, Ansi16)

        application.Run window |> ignore

        window.Projection.Rows |> should haveLength 2

    [<Fact>]
    member _.``renderer uses Terminal Gui v2 instance views and rounded responsive sections``() =
        use application = Application.Create()
        let messages = ResizeArray<Message>()
        use window = new ExplorerWindow(model (), messages.Add, ignore, MidnightViolet)
        window.Frame <- Rectangle(0, 0, 160, 45)
        window.Render(model ())

        application.Initialized |> should equal false
        window.BorderStyle |> should equal LineStyle.Rounded
        window.Context |> should be ofExactType<Markdown>
        window.Projection.Width |> should equal Wide

        window.Frame <- Rectangle(0, 0, 80, 45)
        window.Render(model ())
        window.Projection.Width |> should equal Narrow
        window.ListFrame.Visible |> should equal true
        window.ContextFrame.Visible |> should equal false

        let details =
            { model () with
                Route = Content(PackageDetails direct.Id) }

        window.Render details
        window.ListFrame.Visible |> should equal false
        window.ContextFrame.Visible |> should equal true

        window.Frame <- Rectangle(0, 0, 160, 45)
        window.Render details
        window.ListFrame.Visible |> should equal true
        window.ContextFrame.Visible |> should equal true

    [<Fact>]
    member _.``in-place refresh keeps package selection while changing status and content``() =
        let messages = ResizeArray<Message>()
        let initial = model ()
        use window = new ExplorerWindow(initial, messages.Add, ignore, Ansi16)
        window.PackageList.SelectedItem <- System.Nullable 1

        let refreshing =
            { initial with
                Pending.Refresh = Some(RequestToken 99L)
                Details = Map.empty }

        window.Render refreshing

        window.PackageList.SelectedItem |> should equal (System.Nullable 1)

        (window.Projection.Status).Contains("Refreshing installed packages")
        |> should equal true

        window.Projection.Rows |> should haveLength initial.Packages.Length

    [<Fact>]
    member _.``renderer actions send application messages without owning backend policy``() =
        let messages = ResizeArray<Message>()
        use window = new ExplorerWindow(model (), messages.Add, ignore, MidnightViolet)

        window.Handle(SelectMode Browse)
        window.Handle(ToggleSelection)
        window.Handle(TerminalAction.Refresh)

        messages |> Seq.toList |> should contain (ChangeMode Browse)
        messages |> Seq.toList |> should contain (SetPackageSelection(direct.Id, true))
        messages |> Seq.toList |> should contain Message.Refresh

    [<Fact>]
    member _.``sort and confirmation popovers apply only after explicit activation``() =
        let messages = ResizeArray<Message>()
        let initial = model ()
        use window = new ExplorerWindow(initial, messages.Add, ignore, MidnightViolet)

        window.Handle OpenSort
        window.Handle(MoveHorizontal 1)
        messages |> should be Empty
        window.Handle Activate

        messages
        |> Seq.exists (function
            | ChangeSort sort -> sort.Field = Version
            | _ -> false)
        |> should equal true

        messages.Clear()

        let previewModel =
            { initial with
                Route = Content(OperationPreview(preview, Summary)) }

        window.Render previewModel
        window.Handle Activate
        messages |> should be Empty
        window.Handle Activate
        messages |> Seq.toList |> should contain (ConfirmPreview preview.Id)

    [<Fact>]
    member _.``preview action exposes contextual targets before requesting a solution change``() =
        let messages = ResizeArray<Message>()
        use window = new ExplorerWindow(model (), messages.Add, ignore, MidnightViolet)

        window.Handle Preview

        messages |> Seq.toList |> should equal [ ShowTargeting direct.Id ]

    [<Fact>]
    member _.``targeting actions move between projects and toggle the focused project``() =
        let messages = ResizeArray<Message>()

        let targeting =
            { model () with
                Route = Content(PackageTargeting direct.Id)
                Focus = ProjectRow(ProjectId "Web") }

        use window = new ExplorerWindow(targeting, messages.Add, ignore, MidnightViolet)

        window.Handle(MoveRow 1)
        window.Handle ToggleSelection

        messages
        |> Seq.toList
        |> should contain (SetFocus(ProjectRow(ProjectId "Worker")))

        messages
        |> Seq.toList
        |> should contain (SetProjectSelection(ProjectId "Worker", true))

    [<Fact>]
    member _.``visible controls dispatch modes source prerelease and contextual tabs``() =
        let messages = ResizeArray<Message>()
        use window = new ExplorerWindow(model (), messages.Add, ignore, MidnightViolet)

        window.ModeButtons
        |> List.iter (fun (mode, button) ->
            button.Visible |> should equal true
            button.InvokeCommand(Command.Accept) |> ignore
            messages |> Seq.toList |> should contain (ChangeMode mode))

        window.ModeButtons[1] |> snd |> _.Text |> should equal "[Installed]"

        messages.Clear()
        window.Source.Text <- "private-feed"
        window.Source.InvokeCommand(Command.Accept) |> ignore

        messages
        |> Seq.toList
        |> should contain (SelectSource(Some(PackageSource "private-feed")))

        messages |> Seq.toList |> should contain SubmitSearch

        messages.Clear()
        window.Prerelease.Value <- CheckState.Checked
        messages |> Seq.toList |> should contain (ChangeSearch("", true))
        messages |> Seq.toList |> should contain SubmitSearch

        window.DetailsButton.Visible |> should equal true
        window.ReadmeButton.Visible |> should equal true
        window.DetailsButton.Text |> should equal "[Details]"
        window.ReadmeButton.InvokeCommand(Command.Accept) |> ignore
        messages |> Seq.toList |> should contain (ShowReadme direct.Id)

        messages.Clear()

        window.Render(
            { model () with
                Route = Content(OperationPreview(preview, Summary)) }
        )

        window.DetailsButton.Visible |> should equal false
        window.ReadmeButton.Visible |> should equal false

        window.PreviewButtons
        |> List.iter (fun (tab, button) ->
            button.Visible |> should equal true
            button.InvokeCommand(Command.Accept) |> ignore
            messages |> Seq.toList |> should contain (SelectPreviewTab tab))

        window.PreviewButtons.Head |> snd |> _.Text |> should equal "[Summary]"

    [<Fact>]
    member _.``live key events move focus and dispatch through the fixed window bindings``() =
        use application = Application.Create().Init("ansi")

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
                application.Keyboard.RaiseKeyDownEvent(Key('/')) |> ignore
                window.Search.HasFocus |> should equal true

                application.Keyboard.RaiseKeyDownEvent(Key.L.WithCtrl) |> ignore
                window.Context.HasFocus |> should equal true

                application.Keyboard.RaiseKeyDownEvent(Key.H.WithCtrl) |> ignore
                window.PackageList.HasFocus |> should equal true

                application.Keyboard.RaiseKeyDownEvent(Key.D1) |> ignore
                application.Keyboard.RaiseKeyDownEvent(Key.R) |> ignore

                window.Prerelease.SetFocus() |> ignore
                application.Keyboard.RaiseKeyDownEvent(Key.Space) |> ignore
                application.RequestStop())
        )

        application.Run window |> ignore

        messages |> Seq.toList |> should contain (ChangeMode Browse)
        messages |> Seq.toList |> should contain Message.Refresh
        messages |> Seq.toList |> should contain (ChangeSearch("", true))

    [<Fact>]
    member _.``sort popover reaches every field in both directions before applying``() =
        let fields = [ Relevance; Name; Version; Type ]

        [ Ascending; Descending ]
        |> List.iter (fun direction ->
            fields
            |> List.iter (fun field ->
                let messages = ResizeArray<Message>()
                let initial = model ()
                use window = new ExplorerWindow(initial, messages.Add, ignore, Ansi16)
                window.Handle OpenSort

                let start = fields |> List.findIndex ((=) initial.Sort.Field)
                let destination = fields |> List.findIndex ((=) field)
                let steps = (destination - start + fields.Length) % fields.Length

                [ 1..steps ] |> List.iter (fun _ -> window.Handle(MoveHorizontal 1))

                if direction = Descending then
                    window.Handle(MoveRow 1)

                window.Handle Activate

                messages
                |> Seq.toList
                |> should contain (ChangeSort { Field = field; Direction = direction })))

    [<Fact>]
    member _.``Markdown viewport survives an in-place status refresh``() =
        use application = Application.Create().Init("ansi")

        application.Driver
        |> Option.ofObj
        |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

        application.StopAfterFirstIteration <- true

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
        application.Run window |> ignore

        window.Context.ScrollVertical(4) |> should equal true
        let scrolled = window.Context.Viewport.Y
        scrolled |> should be (greaterThan 0)

        window.Render(
            { initial with
                Pending.Refresh = Some(RequestToken 88L) }
        )

        window.Context.Viewport.Y |> should equal scrolled

    [<Fact>]
    member _.``preview confirmation cancels locally before any apply message is sent``() =
        let messages = ResizeArray<Message>()

        let initial =
            { model () with
                Route = Content(OperationPreview(preview, Summary)) }

        use window = new ExplorerWindow(initial, messages.Add, ignore, MidnightViolet)
        window.Handle Activate
        window.ConfirmationFrame.Visible |> should equal true

        window.Handle Back
        window.ConfirmationFrame.Visible |> should equal false
        messages |> should be Empty

    [<Fact>]
    member _.``progress and failure renders retain package rows and contextual content``() =
        let progress =
            { Preview = preview.Id
              Operation = OperationId "operation-1"
              Status = "Restoring packages"
              Completed = 1
              Total = 3 }

        let progressing =
            { model () with
                Route = Content(OperationProgress progress) }

        use window = new ExplorerWindow(progressing, ignore, ignore, MidnightViolet)
        window.Projection.Rows |> should haveLength 2
        window.Projection.Route |> should equal "Progress"
        window.Projection.Context.Contains("Restoring packages") |> should equal true

        let failure =
            { Scope = BackendSessionFailure
              Kind = BackendUnavailable
              Message = "The backend stopped." }

        window.Render(
            { progressing with
                Route = Failure(OperationProgress progress, BackendSessionFailure)
                Failures = Map.ofList [ BackendSessionFailure, failure ] }
        )

        window.Projection.Rows |> should haveLength 2
        window.Projection.Route |> should equal "Failure"
        window.Projection.Context.Contains(failure.Message) |> should equal true

    [<Fact>]
    member _.``both color profiles render distinct Direct and Central semantic attributes``() =
        [ MidnightViolet; Ansi16 ]
        |> List.iter (fun profile ->
            use application = Application.Create().Init("ansi")

            application.Driver
            |> Option.ofObj
            |> Option.iter (fun driver -> driver.SetScreenSize(120, 35))

            application.StopAfterFirstIteration <- true

            let attributes =
                Collections.Generic.Dictionary<int, Nullable<Terminal.Gui.Drawing.Attribute>>()

            use window = new ExplorerWindow(model (), ignore, application.RequestStop, profile)

            window.PackageList.RowRender.Add(fun args -> attributes[args.Row] <- args.RowAttribute)

            application.Run window |> ignore

            let expected = Theme.schemes profile
            attributes[0] |> should equal (Nullable expected.Direct)
            attributes[1] |> should equal (Nullable expected.Central)
            attributes[0] |> should not' (equal attributes[1])
            window.Projection.Rows[0].Contains("Direct") |> should equal true
            window.Projection.Rows[1].Contains("Central") |> should equal true)

    [<Fact>]
    member _.``back cancels refresh and mode discovery requests before dismissing retained content``
        ()
        =
        [ (fun pending ->
              { pending with
                  Refresh = Some(RequestToken 1L) }),
          RefreshRequest
          (fun pending ->
              { pending with
                  Updates = Some(RequestToken 2L) }),
          UpdatesRequest
          (fun pending ->
              { pending with
                  Consolidation = Some(RequestToken 3L) }),
          ConsolidationRequest ]
        |> List.iter (fun (setPending, request) ->
            let messages = ResizeArray<Message>()

            let initial =
                let current = model ()

                { current with
                    Pending = setPending current.Pending }

            use window = new ExplorerWindow(initial, messages.Add, ignore, Ansi16)
            window.Handle Back
            messages |> Seq.toList |> should equal [ Cancel request ])
