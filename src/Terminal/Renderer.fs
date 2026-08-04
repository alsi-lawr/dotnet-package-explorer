namespace Dotnet.PackageExplorer.Terminal

open System
open System.Collections.ObjectModel
open System.Drawing
open Dotnet.PackageExplorer.Application
open Terminal.Gui.App
open Terminal.Gui.Drawing
open Terminal.Gui.ViewBase
open Terminal.Gui.Views

type ExplorerWindow
    (initial: Model, dispatch: Message -> unit, stop: unit -> unit, profile: ColorProfile) as this =
    inherit Window()

    let schemes = Theme.schemes profile
    let rows = ObservableCollection<string>()
    let mutable model = initial
    let mutable projection = Presentation.project 160 initial
    let mutable rendering = false
    let mutable sortOpen = false
    let mutable pendingSort = initial.Sort
    let mutable confirmationOpen = false
    let mutable narrowContext = false
    let mutable targetIndex = 0

    let modeButton x title =
        new Button(
            X = Pos.Absolute x,
            Y = Pos.Absolute 0,
            Width = Dim.Absolute 16,
            Height = Dim.Absolute 2,
            Text = title,
            NoDecorations = true,
            NoPadding = true,
            BorderStyle = LineStyle.None,
            ShadowStyle = ShadowStyles.None
        )

    let modeButtons =
        [ Browse, modeButton 1 "Browse"
          Installed, modeButton 18 "Installed"
          Updates, modeButton 35 "Updates"
          Consolidate, modeButton 52 "Consolidate" ]

    let modeFrame =
        new FrameView(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Absolute 4,
            BorderStyle = LineStyle.Rounded
        )

    let searchLabel =
        new Label(
            X = Pos.Absolute 1,
            Y = Pos.Absolute 0,
            Width = Dim.Absolute 7,
            Height = Dim.Absolute 1,
            Text = "Query:"
        )

    let search =
        new TextField(
            X = Pos.Absolute 8,
            Y = Pos.Absolute 0,
            Width = Dim.Absolute 36,
            Height = Dim.Absolute 1
        )

    let sourceLabel =
        new Label(
            X = Pos.Absolute 46,
            Y = Pos.Absolute 0,
            Width = Dim.Absolute 8,
            Height = Dim.Absolute 1,
            Text = "Source:"
        )

    let source =
        new TextField(
            X = Pos.Absolute 54,
            Y = Pos.Absolute 0,
            Width = Dim.Absolute 24,
            Height = Dim.Absolute 1
        )

    let prerelease =
        new CheckBox(
            X = Pos.Absolute 80,
            Y = Pos.Absolute 0,
            Width = Dim.Absolute 22,
            Height = Dim.Absolute 1,
            Text = "Include prerelease",
            Value = CheckState.UnChecked
        )

    let searchFrame =
        new FrameView(
            X = Pos.Absolute 0,
            Y = Pos.Bottom modeFrame,
            Width = Dim.Fill(),
            Height = Dim.Absolute 4,
            Title = "Search and filters (/)",
            BorderStyle = LineStyle.Rounded
        )

    let packageList =
        new ListView(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.None,
            ShowMarks = false
        )

    let listFrame =
        new FrameView(
            X = Pos.Absolute 0,
            Y = Pos.Bottom searchFrame,
            Width = Dim.Percent 45,
            Height = Dim.Fill 3,
            BorderStyle = LineStyle.Rounded
        )

    let context =
        new Markdown(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 2,
            Width = Dim.Fill(),
            Height = Dim.Fill 2,
            BorderStyle = LineStyle.None,
            ShowHeadingPrefix = false
        )

    let contextButton x width title =
        new Button(
            X = Pos.Absolute x,
            Y = Pos.Absolute 0,
            Width = Dim.Absolute width,
            Height = Dim.Absolute 2,
            Text = title,
            NoDecorations = true,
            NoPadding = true,
            BorderStyle = LineStyle.None,
            ShadowStyle = ShadowStyles.None,
            Visible = false
        )

    let detailsButton = contextButton 0 14 "Details"
    let readmeButton = contextButton 15 14 "README"

    let previewButtons =
        [ Summary, contextButton 0 13 "Summary"
          Projects, contextButton 14 13 "Projects"
          Dependencies, contextButton 28 17 "Dependencies"
          Files, contextButton 46 11 "Files" ]

    let contextFrame =
        new FrameView(
            X = Pos.Right listFrame,
            Y = Pos.Bottom searchFrame,
            Width = Dim.Fill(),
            Height = Dim.Fill 3,
            BorderStyle = LineStyle.Rounded
        )

    let help =
        new Label(
            X = Pos.Absolute 0,
            Y = Pos.AnchorEnd 3,
            Width = Dim.Fill(),
            Height = Dim.Absolute 2,
            Text =
                "Tab modes | 1-4 jump | j/k rows | h/l tabs | C-h/C-l panes | s sort"
                + "\n/ search | Space select | Enter details | p preview | r refresh | "
                + "Esc back | q quit"
        )

    let route =
        new Label(
            X = Pos.AnchorEnd 24,
            Y = Pos.AnchorEnd 1,
            Width = Dim.Absolute 24,
            Height = Dim.Absolute 1,
            TextAlignment = Alignment.End
        )

    let status =
        new Label(
            X = Pos.Absolute 0,
            Y = Pos.AnchorEnd 1,
            Width = Dim.Fill 24,
            Height = Dim.Absolute 1
        )

    let sortContents =
        new Markdown(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            ShowHeadingPrefix = false
        )

    let sortFrame =
        new FrameView(
            X = Pos.AnchorEnd 36,
            Y = Pos.Absolute 6,
            Width = Dim.Absolute 34,
            Height = Dim.Absolute 9,
            Title = "Sort packages",
            BorderStyle = LineStyle.Rounded,
            Visible = false
        )

    let confirmationContents =
        new Markdown(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            ShowHeadingPrefix = false
        )

    let confirmationFrame =
        new FrameView(
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Absolute 58,
            Height = Dim.Absolute 9,
            Title = "Confirm package change",
            BorderStyle = LineStyle.Rounded,
            Visible = false
        )

    let modeOrder = [ Browse; Installed; Updates; Consolidate ]
    let previewTabs = [ Summary; Projects; Dependencies; Files ]

    let contentRoute () =
        match model.Route with
        | Content value
        | Failure(value, _) -> value

    let activeIndex () =
        model.ActivePackage
        |> Option.bind (fun package ->
            model.Packages |> List.tryFindIndex (fun candidate -> candidate.Id = package))

    let selectedPackage () =
        activeIndex ()
        |> Option.bind (fun index -> model.Packages |> List.tryItem index)

    let targetProjects () = WorkspaceTarget.projects model.Target

    let selectedProject () =
        targetProjects () |> List.tryItem targetIndex

    let cycle values current direction =
        let currentIndex = values |> List.findIndex ((=) current)
        let count = values.Length
        values[(currentIndex + direction + count) % count]

    let renderSort () =
        let field =
            match pendingSort.Field with
            | Relevance -> "Relevance"
            | Name -> "Package"
            | Version -> "Version"
            | Type -> "Type"

        let direction =
            match pendingSort.Direction with
            | Ascending -> "Ascending"
            | Descending -> "Descending"

        sortContents.Text <-
            $"**Field:** {field}\n\n**Direction:** {direction}\n\n"
            + "h/l field | j/k direction | Enter apply | Esc close"

    let updateRows next =
        let expected = next.Rows |> List.toArray
        let unchanged = rows.Count = expected.Length && Seq.forall2 (=) rows expected

        if not unchanged then
            let oldViewport = packageList.Viewport
            rows.Clear()
            expected |> Array.iter rows.Add

            match activeIndex () with
            | Some index when index < rows.Count -> packageList.SelectedItem <- Nullable index
            | _ when rows.Count > 0 -> packageList.SelectedItem <- Nullable 0
            | _ -> packageList.SelectedItem <- Nullable()

            let maximumOffset = max 0 (rows.Count - max 1 oldViewport.Height)
            let restored = min oldViewport.Y maximumOffset

            if oldViewport.Height > 0 then
                packageList.Viewport <-
                    Rectangle(oldViewport.X, restored, oldViewport.Width, oldViewport.Height)

    let setLayout width =
        let narrow = Presentation.width width = Narrow
        let showContext = narrow && narrowContext && projection.Route <> "List"

        if narrow then
            searchLabel.X <- Pos.Absolute 1
            searchLabel.Y <- Pos.Absolute 0
            search.X <- Pos.Absolute 8
            search.Y <- Pos.Absolute 0
            search.Width <- Dim.Fill 2
            sourceLabel.X <- Pos.Absolute 1
            sourceLabel.Y <- Pos.Absolute 1
            source.X <- Pos.Absolute 9
            source.Y <- Pos.Absolute 1
            source.Width <- Dim.Absolute(max 12 (width - 38))
            prerelease.X <- Pos.Absolute(max 32 (width - 24))
            prerelease.Y <- Pos.Absolute 1

            listFrame.X <- Pos.Absolute 0
            listFrame.Width <- Dim.Fill()
            contextFrame.X <- Pos.Absolute 0
            contextFrame.Width <- Dim.Fill()
            listFrame.Visible <- not showContext
            contextFrame.Visible <- showContext
        else
            let queryWidth = max 20 (width * 30 / 100)
            let sourceX = queryWidth + 10
            let sourceWidth = max 12 (width * 20 / 100)

            searchLabel.X <- Pos.Absolute 1
            searchLabel.Y <- Pos.Absolute 0
            search.X <- Pos.Absolute 8
            search.Y <- Pos.Absolute 0
            search.Width <- Dim.Absolute queryWidth
            sourceLabel.X <- Pos.Absolute sourceX
            sourceLabel.Y <- Pos.Absolute 0
            source.X <- Pos.Absolute(sourceX + 8)
            source.Y <- Pos.Absolute 0
            source.Width <- Dim.Absolute sourceWidth
            prerelease.X <- Pos.Absolute(sourceX + sourceWidth + 10)
            prerelease.Y <- Pos.Absolute 0

            listFrame.X <- Pos.Absolute 0
            listFrame.Width <- Dim.Percent 45
            contextFrame.X <- Pos.Right listFrame
            contextFrame.Width <- Dim.Fill()
            listFrame.Visible <- true
            contextFrame.Visible <- true

    let render nextModel =
        let previousContent = contentRoute ()
        rendering <- true
        model <- nextModel
        projection <- Presentation.project this.Viewport.Width model

        modeButtons
        |> List.iter (fun (mode, button) ->
            let title =
                match mode with
                | Browse -> "Browse"
                | Installed -> "Installed"
                | Updates -> "Updates"
                | Consolidate -> "Consolidate"

            button.Text <- if mode = model.Mode then $"[{title}]" else title

            Theme.apply
                (if mode = model.Mode then
                     schemes.Information
                 else
                     schemes.Section)
                button)

        if not search.HasFocus && search.Text <> model.Query.Text then
            search.Text <- model.Query.Text

        let selectedSource =
            model.SelectedSource
            |> Option.map (fun (PackageSource value) -> value)
            |> Option.defaultValue ""

        if not source.HasFocus && source.Text <> selectedSource then
            source.Text <- selectedSource

        let prereleaseValue =
            if model.Query.IncludePrerelease then
                CheckState.Checked
            else
                CheckState.UnChecked

        if prerelease.Value <> prereleaseValue then
            prerelease.Value <- prereleaseValue

        let packageTabs, activeDetailsTab =
            match contentRoute () with
            | PackageList when model.ActivePackage.IsSome -> true, Some "Details"
            | PackageDetails _ -> true, Some "Details"
            | PackageReadme _ -> true, Some "README"
            | _ -> false, None

        detailsButton.Visible <- packageTabs
        readmeButton.Visible <- packageTabs

        detailsButton.Text <-
            if activeDetailsTab = Some "Details" then
                "[Details]"
            else
                "Details"

        readmeButton.Text <-
            if activeDetailsTab = Some "README" then
                "[README]"
            else
                "README"

        Theme.apply
            (if activeDetailsTab = Some "Details" then
                 schemes.Information
             else
                 schemes.Section)
            detailsButton

        Theme.apply
            (if activeDetailsTab = Some "README" then
                 schemes.Information
             else
                 schemes.Section)
            readmeButton

        let activePreviewTab =
            match contentRoute () with
            | OperationPreview(_, tab) -> Some tab
            | _ -> None

        previewButtons
        |> List.iter (fun (tab, button) ->
            button.Visible <- activePreviewTab.IsSome

            let title =
                match tab with
                | Summary -> "Summary"
                | Projects -> "Projects"
                | Dependencies -> "Dependencies"
                | Files -> "Files"

            button.Text <- if activePreviewTab = Some tab then $"[{title}]" else title

            Theme.apply
                (if activePreviewTab = Some tab then
                     schemes.Information
                 else
                     schemes.Section)
                button)

        let tabsVisible = packageTabs || activePreviewTab.IsSome
        context.Y <- Pos.Absolute(if tabsVisible then 2 else 0)
        context.Height <- Dim.Fill(if tabsVisible then 2 else 0)

        listFrame.Title <- projection.ListTitle
        contextFrame.Title <- projection.ContextTitle
        updateRows projection

        if context.Text <> projection.Context then
            let oldViewport = context.Viewport
            context.Text <- projection.Context

            if oldViewport.Height > 0 then
                context.Viewport <- oldViewport

        status.Text <- projection.Status
        route.Text <- projection.Route

        match model.Route with
        | Failure _ ->
            Theme.apply schemes.Failure status
            Theme.apply schemes.Failure context
        | _ when
            model.Pending.Apply.IsSome
            || model.Pending.Preview.IsSome
            || model.Pending.Refresh.IsSome
            || model.Pending.Updates.IsSome
            || model.Pending.Consolidation.IsSome
            || model.Pending.Search.IsSome
            ->
            Theme.apply schemes.Warning status
            Theme.apply schemes.Canvas context
        | _ ->
            Theme.apply schemes.Success status
            Theme.apply schemes.Canvas context

        let nextContent = contentRoute ()

        if nextContent <> previousContent then
            match nextContent with
            | PackageList -> narrowContext <- false
            | _ -> narrowContext <- true

        match model.Focus with
        | ProjectRow project ->
            targetProjects ()
            |> List.tryFindIndex (fun candidate -> candidate.Id = project)
            |> Option.iter (fun index -> targetIndex <- index)
        | _ -> ()

        setLayout this.Viewport.Width
        rendering <- false
        this.SetNeedsDraw()

    let messageForMove direction =
        match contentRoute () with
        | PackageTargeting _ ->
            let projects = targetProjects ()

            if not (List.isEmpty projects) then
                targetIndex <- Math.Clamp(targetIndex + direction, 0, projects.Length - 1)
                dispatch (SetFocus(ProjectRow projects[targetIndex].Id))
        | _ when rows.Count > 0 ->
            let current = packageList.SelectedItem.GetValueOrDefault 0
            let next = Math.Clamp(current + direction, 0, rows.Count - 1)
            packageList.SelectedItem <- Nullable next
        | _ -> ()

    let moveHorizontal direction =
        if sortOpen then
            let fields = [ Relevance; Name; Version; Type ]

            pendingSort <-
                { pendingSort with
                    Field = cycle fields pendingSort.Field direction }

            renderSort ()
        else
            match contentRoute () with
            | PackageDetails package when direction > 0 -> dispatch (ShowReadme package)
            | PackageReadme package when direction < 0 -> dispatch (ShowDetails package)
            | OperationPreview(_, tab) ->
                dispatch (SelectPreviewTab(cycle previewTabs tab direction))
            | _ -> ()

    let requestPreview () =
        let needsTargeting =
            match contentRoute () with
            | PackageTargeting _ -> false
            | _ -> true

        match selectedPackage () with
        | None -> ()
        | Some package when
            WorkspaceTarget.supportsProjectSelection model.Target
            && Set.isEmpty model.TargetSelection.Projects
            && needsTargeting
            ->
            dispatch (ShowTargeting package.Id)
        | Some package ->
            let operation =
                match model.Mode with
                | Browse -> InstallPackage(package.Id, package.LatestVersion)
                | Installed -> UpdateSelectedPackages(Set.singleton package.Id)
                | Updates ->
                    let selected =
                        if Set.isEmpty model.SelectedPackages then
                            Set.singleton package.Id
                        else
                            model.SelectedPackages

                    UpdateSelectedPackages selected
                | Consolidate ->
                    let version =
                        package.LatestVersion
                        |> Option.orElse package.InstalledVersion
                        |> Option.defaultValue (PackageVersion "")

                    ConsolidatePackage(package.Id, version)

            dispatch (RequestPreview operation)

    let dismissOrCancel () =
        if sortOpen then
            sortOpen <- false
            sortFrame.Visible <- false
        elif confirmationOpen then
            confirmationOpen <- false
            confirmationFrame.Visible <- false
        else
            match model.Route with
            | _ when model.Pending.Apply.IsSome -> dispatch (Cancel ApplyRequest)
            | _ when model.Pending.Preview.IsSome -> dispatch (Cancel PreviewRequest)
            | _ when model.Pending.Readme.IsSome -> dispatch (Cancel ReadmeRequest)
            | _ when model.Pending.Details.IsSome -> dispatch (Cancel DetailsRequest)
            | _ when model.Pending.Updates.IsSome -> dispatch (Cancel UpdatesRequest)
            | _ when model.Pending.Consolidation.IsSome -> dispatch (Cancel ConsolidationRequest)
            | _ when model.Pending.Refresh.IsSome -> dispatch (Cancel RefreshRequest)
            | _ when model.Pending.Search.IsSome -> dispatch (Cancel SearchRequest)
            | Failure(_, scope) -> dispatch (DismissFailure scope)
            | _ ->
                narrowContext <- false
                packageList.SetFocus() |> ignore
                setLayout this.Viewport.Width

    let handle (action: TerminalAction) =
        match action with
        | NextMode -> dispatch (ChangeMode(cycle modeOrder model.Mode 1))
        | PreviousMode -> dispatch (ChangeMode(cycle modeOrder model.Mode -1))
        | SelectMode mode -> dispatch (ChangeMode mode)
        | MoveRow _ when sortOpen ->
            pendingSort <-
                { pendingSort with
                    Direction =
                        match pendingSort.Direction with
                        | Ascending -> Descending
                        | Descending -> Ascending }

            renderSort ()
        | MoveRow direction -> messageForMove direction
        | MoveHorizontal direction -> moveHorizontal direction
        | MovePane direction ->
            if Presentation.width this.Viewport.Width = Narrow then
                narrowContext <- direction > 0
                setLayout this.Viewport.Width

                if narrowContext then
                    context.SetFocus() |> ignore
                else
                    packageList.SetFocus() |> ignore
            elif direction > 0 then
                context.SetFocus() |> ignore
            else
                packageList.SetFocus() |> ignore
        | OpenSort ->
            pendingSort <- model.Sort
            sortOpen <- true
            renderSort ()
            sortFrame.Visible <- true
            sortFrame.SetFocus() |> ignore
        | FocusSearch -> search.SetFocus() |> ignore
        | ToggleSelection ->
            match contentRoute () with
            | PackageTargeting _ ->
                selectedProject ()
                |> Option.iter (fun project ->
                    let selected = not (model.TargetSelection.Projects.Contains project.Id)
                    dispatch (SetProjectSelection(project.Id, selected)))
            | _ ->
                selectedPackage ()
                |> Option.iter (fun package ->
                    let selected = not (model.SelectedPackages.Contains package.Id)
                    dispatch (SetPackageSelection(package.Id, selected)))
        | Activate when sortOpen ->
            sortOpen <- false
            sortFrame.Visible <- false
            dispatch (ChangeSort pendingSort)
            packageList.SetFocus() |> ignore
        | Activate when confirmationOpen ->
            confirmationOpen <- false
            confirmationFrame.Visible <- false

            match contentRoute () with
            | OperationPreview(preview, _) -> dispatch (ConfirmPreview preview.Id)
            | _ -> ()
        | Activate when search.HasFocus ->
            dispatch (ChangeSearch(search.Text, model.Query.IncludePrerelease))
            dispatch SubmitSearch
        | Activate when source.HasFocus ->
            let value =
                if String.IsNullOrWhiteSpace source.Text then
                    None
                else
                    Some(PackageSource(source.Text.Trim()))

            dispatch (SelectSource value)
            dispatch SubmitSearch
        | Activate when prerelease.HasFocus -> prerelease.AdvanceCheckState() |> ignore
        | Activate ->
            match contentRoute () with
            | OperationPreview(preview, _) ->
                confirmationOpen <- true

                confirmationContents.Text <-
                    $"# Apply this preview?\n\n{String.concat Environment.NewLine preview.Summary}"
                    + "\n\nEnter confirms | Esc cancels"

                confirmationFrame.Visible <- true
                confirmationFrame.SetFocus() |> ignore
            | _ ->
                selectedPackage ()
                |> Option.iter (fun package ->
                    narrowContext <- true
                    dispatch (ShowDetails package.Id))
        | Preview -> requestPreview ()
        | RefreshPackages -> dispatch Refresh
        | Back -> dismissOrCancel ()
        | Quit -> stop ()

    let handleKey key =
        let isTextInput = search.HasFocus || source.HasFocus

        let buttons =
            [ detailsButton; readmeButton ]
            @ (modeButtons |> List.map snd)
            @ (previewButtons |> List.map snd)

        let allowedWhileEditing =
            function
            | NextMode
            | PreviousMode
            | MovePane _
            | Back -> true
            | _ -> false

        let usesNativeActivation =
            function
            | Activate
            | ToggleSelection ->
                prerelease.HasFocus
                || List.exists (fun (button: Button) -> button.HasFocus) buttons
            | _ -> false

        match Keyboard.action key with
        | Some action when usesNativeActivation action -> ()
        | Some action when not isTextInput || allowedWhileEditing action ->
            handle action
            key.Handled <- true
        | _ -> ()

    let bindKeyboard (keyboard: IKeyboard) = keyboard.KeyDown.Subscribe handleKey

    let bindFallbackKeys (view: View) =
        view.KeyDown.Add(fun key ->
            match Keyboard.action key with
            | Some action ->
                handle action
                key.Handled <- true
            | None -> ())

    do
        this.Title <- "WORKSPACE EXPLORER / PACKAGES"
        this.BorderStyle <- LineStyle.Rounded
        this.Width <- Dim.Fill()
        this.Height <- Dim.Fill()
        this.ShadowStyle <- ShadowStyles.None

        modeButtons |> List.iter (snd >> modeFrame.Add >> ignore)
        searchFrame.Add searchLabel |> ignore
        searchFrame.Add search |> ignore
        searchFrame.Add sourceLabel |> ignore
        searchFrame.Add source |> ignore
        searchFrame.Add prerelease |> ignore
        listFrame.Add packageList |> ignore
        contextFrame.Add detailsButton |> ignore
        contextFrame.Add readmeButton |> ignore
        previewButtons |> List.iter (snd >> contextFrame.Add >> ignore)
        contextFrame.Add context |> ignore
        sortFrame.Add sortContents |> ignore
        confirmationFrame.Add confirmationContents |> ignore

        this.Add modeFrame |> ignore
        this.Add searchFrame |> ignore
        this.Add listFrame |> ignore
        this.Add contextFrame |> ignore
        this.Add help |> ignore
        this.Add status |> ignore
        this.Add route |> ignore
        this.Add sortFrame |> ignore
        this.Add confirmationFrame |> ignore

        Theme.apply schemes.Canvas this
        Theme.apply schemes.Section modeFrame
        Theme.apply schemes.Section searchFrame
        Theme.apply schemes.Section listFrame
        Theme.apply schemes.Section contextFrame
        Theme.apply schemes.Canvas packageList
        Theme.apply schemes.Canvas context
        Theme.apply schemes.Information searchLabel
        Theme.apply schemes.Information sourceLabel
        Theme.apply schemes.Section search
        Theme.apply schemes.Section source
        Theme.apply schemes.Section prerelease
        Theme.apply schemes.Information route
        Theme.apply schemes.Success status
        Theme.apply schemes.Section sortFrame
        Theme.apply schemes.Section confirmationFrame

        packageList.SetSource rows

        packageList.ViewportSettings <-
            packageList.ViewportSettings ||| ViewportSettingsFlags.HasVerticalScrollBar

        context.ViewportSettings <-
            context.ViewportSettings ||| ViewportSettingsFlags.HasVerticalScrollBar

        packageList.RowRender.Add(fun args ->
            model.Packages
            |> List.tryItem args.Row
            |> Option.bind _.Kind
            |> Option.iter (fun kind ->
                match kind with
                | Direct -> args.RowAttribute <- schemes.Direct
                | Central -> args.RowAttribute <- schemes.Central
                | Transitive
                | Framework -> ()))

        packageList.ValueChanged.Add(fun args ->
            if not rendering then
                args.NewValue
                |> Option.ofNullable
                |> Option.bind (fun index -> model.Packages |> List.tryItem index)
                |> Option.iter (fun package -> dispatch (SelectPackage package.Id)))

        modeButtons
        |> List.iter (fun (mode, button) ->
            button.Accepting.Add(fun _ -> dispatch (ChangeMode mode)))

        detailsButton.Accepting.Add(fun _ ->
            selectedPackage ()
            |> Option.iter (fun package -> dispatch (ShowDetails package.Id)))

        readmeButton.Accepting.Add(fun _ ->
            selectedPackage ()
            |> Option.iter (fun package -> dispatch (ShowReadme package.Id)))

        previewButtons
        |> List.iter (fun (tab, button) ->
            button.Accepting.Add(fun _ -> dispatch (SelectPreviewTab tab)))

        source.Accepting.Add(fun _ ->
            let value =
                if String.IsNullOrWhiteSpace source.Text then
                    None
                else
                    Some(PackageSource(source.Text.Trim()))

            dispatch (SelectSource value)
            dispatch SubmitSearch)

        search.Accepting.Add(fun _ ->
            dispatch (ChangeSearch(search.Text, model.Query.IncludePrerelease))
            dispatch SubmitSearch)

        prerelease.ValueChanged.Add(fun args ->
            if not rendering then
                dispatch (ChangeSearch(search.Text, args.NewValue = CheckState.Checked))
                dispatch SubmitSearch)

        bindFallbackKeys this

        this.ViewportChanged.Add(fun _ ->
            projection <- Presentation.project this.Viewport.Width model
            setLayout this.Viewport.Width)

        render initial

    member _.Render nextModel = render nextModel
    member _.BindKeyboard keyboard = bindKeyboard keyboard
