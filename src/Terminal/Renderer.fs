namespace Dotnet.PackageExplorer.Terminal

open System
open System.Collections.ObjectModel
open System.Data
open System.Drawing
open Dotnet.PackageExplorer.Application
open Terminal.Gui.App
open Terminal.Gui.Drawing
open Terminal.Gui.ViewBase
open Terminal.Gui.Views

type private SortFocus =
    | SortField
    | SortDirection

type ExplorerWindow
    (initial: Model, dispatch: Message -> unit, stop: unit -> unit, profile: ColorProfile) as this =
    inherit Window()

    let schemes = Theme.schemes profile
    let contentScheme = Scheme(schemes.Canvas, Code = schemes.Canvas.Normal)

    let passiveContentScheme =
        Scheme(
            Normal = schemes.Canvas.Normal,
            Focus = schemes.Canvas.Normal,
            Active = schemes.Canvas.Normal,
            Editable = schemes.Canvas.Normal,
            Highlight = schemes.Canvas.Normal
        )

    let rows = ObservableCollection<string>()
    let mutable model = initial
    let mutable projection = Presentation.project 160 initial
    let mutable rendering = false
    let mutable sortOpen = false
    let mutable pendingSort = initial.Sort
    let mutable sortFocus = SortField
    let mutable sortReturnFocus: View option = None
    let mutable confirmationOpen = false
    let mutable helpOpen = false
    let mutable helpReturnFocus: View option = None
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
            Y = Pos.Absolute 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.None,
            ShowMarks = false
        )

    let listHeading =
        new Label(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Absolute 1
        )

    let listNotice =
        new Label(
            X = Pos.Absolute 1,
            Y = Pos.Absolute 1,
            Width = Dim.Fill 2,
            Height = Dim.Absolute 2,
            Visible = false
        )

    let listFrame =
        new FrameView(
            X = Pos.Absolute 0,
            Y = Pos.Bottom searchFrame,
            Width = Dim.Percent Presentation.WideListPercentage,
            Height = Dim.Fill 2,
            BorderStyle = LineStyle.Rounded
        )

    let context =
        new Markdown(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 2,
            Width = Dim.Fill(),
            Height = Dim.Fill 2,
            BorderStyle = LineStyle.None,
            ShowHeadingPrefix = false,
            ShowCopyButtons = false
        )

    let projectTableData =
        let table = new DataTable()
        table.Columns.Add "Project" |> ignore
        table.Columns.Add "Framework" |> ignore
        table.Columns.Add "Current" |> ignore
        table.Columns.Add "Proposed" |> ignore
        table

    let projectTable =
        new TableView(
            new DataTableSource(projectTableData),
            X = Pos.Absolute 0,
            Y = Pos.Absolute 2,
            Width = Dim.Fill(),
            Height = Dim.Fill 2,
            BorderStyle = LineStyle.None,
            Visible = false
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
            Height = Dim.Fill 2,
            BorderStyle = LineStyle.Rounded
        )

    let guidance =
        new Label(
            X = Pos.Absolute 0,
            Y = Pos.AnchorEnd 2,
            Width = Dim.Fill(),
            Height = Dim.Absolute 1,
            Text = ""
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

    let sortBackdrop =
        new View(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            Visible = false
        )

    let sortContents =
        new Label(
            X = Pos.Absolute 2,
            Y = Pos.Absolute 1,
            Width = Dim.Fill 2,
            Height = Dim.Fill 1,
            CanFocus = false
        )

    let sortFrame =
        new FrameView(
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Absolute 48,
            Height = Dim.Absolute 9,
            Title = "[s] Sort packages",
            BorderStyle = LineStyle.Rounded,
            Visible = false
        )

    let confirmationContents =
        new Markdown(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Fill 2,
            CanFocus = false,
            ShowHeadingPrefix = false
        )

    let confirmationActions =
        new Label(
            X = Pos.Absolute 1,
            Y = Pos.AnchorEnd 1,
            Width = Dim.Fill 1,
            Height = Dim.Absolute 1,
            Text = "[Enter] Apply    [Esc] Cancel    [?] Help"
        )

    let confirmationFrame =
        new FrameView(
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Absolute 58,
            Height = Dim.Absolute 12,
            Title = "Apply package changes?",
            BorderStyle = LineStyle.Rounded,
            Visible = false
        )

    let confirmationBackdrop =
        new View(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            Visible = false
        )

    let clearConfirmation () =
        confirmationOpen <- false
        confirmationBackdrop.Visible <- false
        confirmationFrame.Visible <- false

    let showConfirmation () =
        confirmationOpen <- true
        confirmationBackdrop.Visible <- true
        confirmationFrame.Visible <- true
        confirmationFrame.SetFocus() |> ignore

    let helpContents =
        new Label(
            X = Pos.Absolute 1,
            Y = Pos.Absolute 0,
            Width = Dim.Fill 1,
            Height = Dim.Fill(),
            CanFocus = false
        )

    let helpBackdrop =
        new View(
            X = Pos.Absolute 0,
            Y = Pos.Absolute 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            Visible = false
        )

    let helpFrame =
        new FrameView(
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Absolute 76,
            Height = Dim.Absolute 23,
            Title = "Help",
            BorderStyle = LineStyle.Rounded,
            Visible = false
        )

    let modeOrder = [ Browse; Installed; Updates; Consolidate ]
    let previewTabs = [ Summary; Projects; Dependencies; Files ]

    let contentRoute () =
        match model.Route with
        | Content value
        | Failure(value, _) -> value

    let operationOwnsInput () =
        confirmationOpen || Presentation.ownsInput model

    let backgroundInputBlocked () =
        helpOpen || sortOpen || operationOwnsInput ()

    let readmeCodeWidth columns =
        match Presentation.width columns with
        | Narrow -> max 20 (columns - 8)
        | Wide -> max 20 (columns - (columns * Presentation.listPercentage model / 100) - 8)

    let wrapCodeLine width (line: string) =
        let rec wrap (remaining: string) =
            if remaining.Length <= width then
                [ remaining ]
            else
                let whitespace = remaining.LastIndexOfAny([| ' '; '\t' |], width - 1, width)

                let take = if whitespace > 0 then whitespace + 1 else width
                remaining[.. take - 1] :: wrap remaining[take..]

        wrap line

    let fencedReadme columns (source: string) =
        let fence (line: string) =
            let candidate = line.TrimStart()
            let indent = line.Length - candidate.Length

            if indent <= 3 && candidate.Length >= 3 then
                let marker = candidate[0]

                if marker = '`' || marker = '~' then
                    let length = candidate |> Seq.takeWhile ((=) marker) |> Seq.length

                    if length >= 3 then
                        Some(marker, length, candidate[length..])
                    else
                        None
                else
                    None
            else
                None

        let closes (marker, length) line =
            match fence line with
            | Some(candidateMarker, candidateLength, remainder) ->
                candidateMarker = marker
                && candidateLength >= length
                && String.IsNullOrWhiteSpace remainder
            | None -> false

        let width = readmeCodeWidth columns

        let lines =
            source.Split('\n')
            |> Array.fold
                (fun (activeFence, rendered) line ->
                    match activeFence with
                    | Some current when closes current line -> None, line :: rendered
                    | Some current ->
                        Some current, (wrapCodeLine width line |> List.rev) @ rendered
                    | None ->
                        match fence line with
                        | Some(marker, length, _) -> Some(marker, length), line :: rendered
                        | None -> None, line :: rendered)
                (None, [])
            |> snd
            |> List.rev

        String.concat "\n" lines

    let contextText columns =
        match model.Route with
        | Content(PackageReadme _) -> fencedReadme columns projection.Context
        | Content _
        | Failure _ -> projection.Context

    let updateContextText columns =
        let next = contextText columns

        if context.Text <> next then
            let oldViewport = context.Viewport
            context.Text <- next

            if oldViewport.Height > 0 then
                context.Viewport <- oldViewport

    let activeContextView () : View =
        if projectTable.Visible then projectTable else context

    let projectRows () =
        match model.Route with
        | Content(OperationPreview(preview, Projects)) -> preview.Projects
        | _ -> []

    let updateProjectTable () =
        projectTableData.Rows.Clear()

        projectRows ()
        |> List.iter (fun project ->
            let (ProjectId name) = project.Project

            let framework =
                project.Framework
                |> Option.map (fun (TargetFramework value) -> value)
                |> Option.defaultValue "(all)"

            projectTableData.Rows.Add(
                name,
                framework,
                Presentation.packageVersion project.Before,
                Presentation.packageVersion project.After
            )
            |> ignore)

        projectTable.Update()

    let projectColumnWidths availableWidth =
        let textWidth (heading: string) (values: string list) =
            values |> List.map String.length |> List.fold max heading.Length

        let rows = projectRows ()

        let projectWidth =
            rows
            |> List.map (fun project ->
                let (ProjectId value) = project.Project
                value)
            |> textWidth "Project"

        let frameworkWidth =
            rows
            |> List.map (fun project ->
                project.Framework
                |> Option.map (fun (TargetFramework value) -> value)
                |> Option.defaultValue "(all)")
            |> textWidth "Framework"

        let versionWidth =
            [ rows |> List.map (_.Before >> Presentation.packageVersion)
              rows |> List.map (_.After >> Presentation.packageVersion) ]
            |> List.concat
            |> textWidth "Proposed"

        let baseProject = max projectWidth versionWidth
        let usableWidth = max 1 (availableWidth - 3)
        let required = baseProject + frameworkWidth + versionWidth * 2
        let extra = max 0 (usableWidth - required)
        let projectExtra = extra / 3
        let frameworkExtra = extra - projectExtra

        baseProject + projectExtra, frameworkWidth + frameworkExtra, versionWidth

    let updateProjectColumns availableWidth =
        if availableWidth > 0 then
            let projectWidth, frameworkWidth, versionWidth = projectColumnWidths availableWidth

            [ 0, projectWidth; 1, frameworkWidth; 2, versionWidth; 3, versionWidth ]
            |> List.iter (fun (column, width) ->
                let style = projectTable.Style.GetOrCreateColumnStyle column
                style.MinWidth <- width
                style.MaxWidth <- width)

            projectTable.MaxCellWidth <- max projectWidth (max frameworkWidth versionWidth)

            projectTable.Update()

    let currentActions () =
        if sortOpen then
            "h/l field | j/k direction | Enter apply | Esc close | ? Help"
        elif confirmationOpen then
            "Enter apply | Esc cancel | ? Help"
        else
            projection.Actions

    let renderHelp () =
        helpContents.Text <-
            "Current actions\n"
            + currentActions ()
            + "\n\nNavigation\n"
            + "Tab / Shift-Tab  Next / previous mode\n"
            + "1-4              Browse / Installed / Updates / Consolidate\n"
            + "j / k            Move down / up\n"
            + "h / l            Previous / next tab\n"
            + "Ctrl-h / Ctrl-l  Previous / next pane\n\n"
            + "Packages\n"
            + "s                Sort\n"
            + "/                Search\n"
            + "Space            Select\n"
            + "Enter            Open or confirm\n"
            + "p                Preview\n"
            + "r                Refresh\n\n"
            + "Esc              Back or cancel outside Help\n"
            + "? / Esc          Close help\n"
            + "q                Quit"

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

        let marker control =
            if sortFocus = control then ">" else " "

        sortContents.Text <-
            $"{marker SortField} Field       {field}\n"
            + $"{marker SortDirection} Direction   {direction}\n\n"
            + "h/l Change field    j/k Change direction\n"
            + "Enter Apply    Esc Close    ? Help"

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

    let updateListState next =
        listHeading.Text <- next.ListHeading

        match next.ListNotice with
        | Some notice ->
            listNotice.Text <- notice
            listNotice.Visible <- true

            if List.isEmpty next.Rows then
                packageList.Visible <- false
            else
                packageList.Y <- Pos.Absolute 3
                packageList.Height <- Dim.Fill()
                packageList.Visible <- true
        | None ->
            listNotice.Visible <- false
            packageList.Y <- Pos.Absolute 1
            packageList.Height <- Dim.Fill()
            packageList.Visible <- true

    let setLayout width =
        let narrow = Presentation.width width = Narrow

        let showContext =
            match contentRoute () with
            | PackageList -> false
            | _ -> narrow && narrowContext

        let operation = Presentation.ownsInput model

        modeFrame.Visible <- not operation
        searchFrame.Visible <- not operation

        if operation then
            let surfaceWidth = min 76 (max 40 (width - 8))
            let surfaceHeight = min 14 (max 10 (this.Viewport.Height - 6))

            listFrame.Visible <- false
            contextFrame.Visible <- true
            contextFrame.X <- Pos.Center()
            contextFrame.Y <- Pos.Center()
            contextFrame.Width <- Dim.Absolute surfaceWidth
            contextFrame.Height <- Dim.Absolute surfaceHeight
        elif narrow then
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
            contextFrame.Y <- Pos.Bottom searchFrame
            contextFrame.Width <- Dim.Fill()
            contextFrame.Height <- Dim.Fill 2
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
            listFrame.Width <- Dim.Percent(Presentation.listPercentage model)
            contextFrame.X <- Pos.Right listFrame
            contextFrame.Y <- Pos.Bottom searchFrame
            contextFrame.Width <- Dim.Fill()
            contextFrame.Height <- Dim.Fill 2
            listFrame.Visible <- true
            contextFrame.Visible <- true

        let overlayWidth = min 76 (max 40 (width - 4))
        helpFrame.Width <- Dim.Absolute overlayWidth

        let tabsOffset =
            match contentRoute () with
            | PackageDetails _
            | PackageReadme _
            | OperationPreview _ -> 2
            | _ -> 0

        let availableWidth =
            if contextFrame.Viewport.Width > 0 then
                contextFrame.Viewport.Width
            elif narrow || operation then
                max 1 (width - 2)
            else
                max 1 (width - width * Presentation.listPercentage model / 100 - 2)

        let availableHeight =
            if contextFrame.Viewport.Height > tabsOffset then
                contextFrame.Viewport.Height - tabsOffset
            else
                max 1 (this.Viewport.Height - 12 - tabsOffset)

        let centered =
            not narrow
            && match model.Route with
               | Content(PackageDetails _)
               | Content(OperationPreview(_, Summary))
               | Content(OperationPreview(_, Dependencies)) -> true
               | _ -> false

        if operation then
            context.X <- Pos.Absolute 1
            context.Y <- Pos.Absolute 1
            context.Width <- Dim.Fill 2
            context.Height <- Dim.Fill 2
        elif centered then
            let bodyWidth = min 64 availableWidth
            let bodyHeight = min 12 availableHeight
            context.X <- Pos.Absolute(max 0 ((availableWidth - bodyWidth) / 2))
            context.Y <- Pos.Absolute(tabsOffset + max 0 ((availableHeight - bodyHeight) / 2))
            context.Width <- Dim.Absolute bodyWidth
            context.Height <- Dim.Absolute bodyHeight
        else
            context.X <- Pos.Absolute 0
            context.Y <- Pos.Absolute tabsOffset
            context.Width <- Dim.Fill()
            context.Height <- Dim.Fill tabsOffset

        projectTable.X <- Pos.Absolute 0
        projectTable.Y <- Pos.Absolute tabsOffset
        projectTable.Width <- Dim.Fill()
        projectTable.Height <- Dim.Fill tabsOffset
        updateProjectColumns availableWidth

    let render nextModel =
        let previousContent = contentRoute ()
        rendering <- true
        model <- nextModel

        let incomingOwnedFailure =
            match model.Route with
            | Failure _ -> Presentation.ownsInput model
            | Content _ -> false

        if incomingOwnedFailure then
            if confirmationOpen then
                clearConfirmation ()

            if sortOpen then
                sortOpen <- false
                sortBackdrop.Visible <- false
                sortFrame.Visible <- false
                sortReturnFocus <- None

            helpOpen <- false
            helpBackdrop.Visible <- false
            helpFrame.Visible <- false
            helpReturnFocus <- None

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
            match model.Route, contentRoute () with
            | Failure _, _ when Presentation.ownsInput model -> false, None
            | _, route ->
                match route with
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
            match model.Route, contentRoute () with
            | Failure _, _ when Presentation.ownsInput model -> None
            | _, OperationPreview(_, tab) -> Some tab
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

        let projectsVisible =
            match model.Route with
            | Content(OperationPreview(_, Projects)) -> true
            | _ -> false

        context.Visible <- not projectsVisible
        projectTable.Visible <- projectsVisible

        listFrame.Title <- projection.ListTitle
        contextFrame.Title <- projection.ContextTitle
        updateRows projection
        updateListState projection
        updateContextText this.Viewport.Width
        updateProjectTable ()

        status.Text <- projection.Status
        route.Text <- projection.Route
        guidance.Text <- currentActions ()

        if confirmationOpen then
            status.Text <- "Package changes are awaiting confirmation"
            route.Text <- "Packages / Confirmation"

        if helpOpen then
            renderHelp ()

        match model.Route with
        | Failure _ ->
            Theme.apply schemes.Failure status
            Theme.apply schemes.Failure context
        | _ when
            confirmationOpen
            || model.Pending.Apply.IsSome
            || model.Pending.Preview.IsSome
            || model.Pending.Refresh.IsSome
            || model.Pending.Updates.IsSome
            || model.Pending.Consolidation.IsSome
            || model.Pending.Search.IsSome
            ->
            Theme.apply schemes.Warning status
            Theme.apply contentScheme context
        | _ ->
            Theme.apply schemes.Success status
            Theme.apply contentScheme context

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
        | _ when projection.ListIsInteractive && rows.Count > 0 ->
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

        match selectedPackage () |> Option.filter (fun _ -> projection.ListIsInteractive) with
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
            sortBackdrop.Visible <- false
            sortFrame.Visible <- false
            sortReturnFocus |> Option.iter (fun view -> view.SetFocus() |> ignore)
            sortReturnFocus <- None
        elif confirmationOpen then
            clearConfirmation ()
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

    let openHelp () =
        helpReturnFocus <- this.MostFocused |> Option.ofObj
        helpOpen <- true
        renderHelp ()
        helpBackdrop.Visible <- true
        helpFrame.Visible <- true
        helpFrame.SetFocus() |> ignore

    let closeHelp () =
        helpOpen <- false
        helpBackdrop.Visible <- false
        helpFrame.Visible <- false

        match helpReturnFocus with
        | Some view -> view.SetFocus() |> ignore
        | None when confirmationOpen -> confirmationFrame.SetFocus() |> ignore
        | None when Presentation.ownsInput model -> activeContextView().SetFocus() |> ignore
        | None -> ()

        helpReturnFocus <- None

    let restoreProjectionChrome () =
        guidance.Text <- projection.Actions
        status.Text <- projection.Status
        route.Text <- projection.Route
        Theme.apply schemes.Success status

    let showConfirmationChrome () =
        guidance.Text <- currentActions ()
        status.Text <- "Package changes are awaiting confirmation"
        route.Text <- "Packages / Confirmation"
        Theme.apply schemes.Warning status

    let handleOwnedAction action =
        if helpOpen then
            match action with
            | ShowHelp
            | Back -> closeHelp ()
            | _ -> ()
        elif confirmationOpen then
            match action with
            | Activate ->
                clearConfirmation ()
                guidance.Text <- "? Help"
                status.Text <- "Applying package changes..."
                route.Text <- "Packages / Applying"
                Theme.apply schemes.Warning status

                match contentRoute () with
                | OperationPreview(preview, _) -> dispatch (ConfirmPreview preview.Id)
                | _ -> ()
            | Back ->
                clearConfirmation ()
                restoreProjectionChrome ()
            | ShowHelp -> openHelp ()
            | _ -> ()
        elif sortOpen then
            match action with
            | MoveRow _ ->
                sortFocus <- SortDirection

                pendingSort <-
                    { pendingSort with
                        Direction =
                            match pendingSort.Direction with
                            | Ascending -> Descending
                            | Descending -> Ascending }

                renderSort ()
            | MoveHorizontal direction ->
                sortFocus <- SortField
                let fields = [ Relevance; Name; Version; Type ]

                pendingSort <-
                    { pendingSort with
                        Field = cycle fields pendingSort.Field direction }

                renderSort ()
            | Activate ->
                sortOpen <- false
                sortBackdrop.Visible <- false
                sortFrame.Visible <- false
                dispatch (ChangeSort pendingSort)
                sortReturnFocus |> Option.iter (fun view -> view.SetFocus() |> ignore)
                sortReturnFocus <- None
            | Back ->
                sortOpen <- false
                sortBackdrop.Visible <- false
                sortFrame.Visible <- false
                sortReturnFocus |> Option.iter (fun view -> view.SetFocus() |> ignore)
                sortReturnFocus <- None
            | ShowHelp -> openHelp ()
            | _ -> ()
        else
            match model.Route, action with
            | Failure(_, scope), Back -> dispatch (DismissFailure scope)
            | _, ShowHelp -> openHelp ()
            | _ -> ()

    let handleUnowned (action: TerminalAction) =
        match action with
        | NextMode -> dispatch (ChangeMode(cycle modeOrder model.Mode 1))
        | PreviousMode -> dispatch (ChangeMode(cycle modeOrder model.Mode -1))
        | SelectMode mode -> dispatch (ChangeMode mode)
        | MoveRow direction -> messageForMove direction
        | MoveHorizontal direction -> moveHorizontal direction
        | MovePane direction ->
            if Presentation.width this.Viewport.Width = Narrow then
                narrowContext <- direction > 0
                setLayout this.Viewport.Width

                if narrowContext then
                    activeContextView().SetFocus() |> ignore
                else
                    packageList.SetFocus() |> ignore
            elif direction > 0 then
                activeContextView().SetFocus() |> ignore
            else
                packageList.SetFocus() |> ignore
        | OpenSort when not projection.ListIsInteractive -> ()
        | OpenSort ->
            pendingSort <- model.Sort
            sortFocus <- SortField
            sortReturnFocus <- this.MostFocused |> Option.ofObj
            sortOpen <- true
            renderSort ()
            sortBackdrop.Visible <- true
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
                |> Option.filter (fun _ -> projection.ListIsInteractive)
                |> Option.iter (fun package ->
                    let selected = not (model.SelectedPackages.Contains package.Id)
                    dispatch (SetPackageSelection(package.Id, selected)))
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
                showConfirmationChrome ()

                confirmationContents.Text <-
                    "Impact summary\n\n"
                    + (preview.Summary
                       |> List.map (fun summary -> $"- {summary}")
                       |> String.concat Environment.NewLine)

                showConfirmation ()
            | _ ->
                selectedPackage ()
                |> Option.filter (fun _ -> projection.ListIsInteractive)
                |> Option.iter (fun package ->
                    narrowContext <- true
                    dispatch (ShowDetails package.Id))
        | Preview -> requestPreview ()
        | RefreshPackages -> dispatch Refresh
        | ShowHelp -> openHelp ()
        | Back -> dismissOrCancel ()
        | Quit -> stop ()


    let handle action =
        if backgroundInputBlocked () then
            handleOwnedAction action
        else
            handleUnowned action

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
            | ShowHelp
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
        | Some action when
            backgroundInputBlocked () || not isTextInput || allowedWhileEditing action
            ->
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
        Glyphs.Stipple <- Text.Rune ' '
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
        listFrame.Add listHeading |> ignore
        listFrame.Add listNotice |> ignore
        listFrame.Add packageList |> ignore
        contextFrame.Add detailsButton |> ignore
        contextFrame.Add readmeButton |> ignore
        previewButtons |> List.iter (snd >> contextFrame.Add >> ignore)
        contextFrame.Add context |> ignore
        contextFrame.Add projectTable |> ignore
        sortFrame.Add sortContents |> ignore
        confirmationFrame.Add confirmationContents |> ignore
        confirmationFrame.Add confirmationActions |> ignore

        this.Add modeFrame |> ignore
        this.Add searchFrame |> ignore
        this.Add listFrame |> ignore
        this.Add contextFrame |> ignore
        this.Add guidance |> ignore
        this.Add status |> ignore
        this.Add route |> ignore
        this.Add sortBackdrop |> ignore
        this.Add sortFrame |> ignore
        this.Add confirmationBackdrop |> ignore
        this.Add confirmationFrame |> ignore
        this.Add helpBackdrop |> ignore
        helpFrame.Add helpContents |> ignore
        this.Add helpFrame |> ignore

        Theme.apply schemes.Section this
        Theme.apply schemes.Muted modeFrame
        Theme.apply schemes.Muted searchFrame
        Theme.apply schemes.Section listFrame
        Theme.apply schemes.Section contextFrame
        Theme.apply schemes.Canvas packageList
        Theme.apply schemes.Information listHeading
        Theme.apply schemes.Warning listNotice
        Theme.apply contentScheme context
        Theme.apply passiveContentScheme projectTable
        Theme.apply schemes.Information searchLabel
        Theme.apply schemes.Information sourceLabel
        Theme.apply schemes.Section search
        Theme.apply schemes.Section source
        Theme.apply schemes.Section prerelease
        Theme.apply schemes.Muted guidance
        Theme.apply schemes.Information route
        Theme.apply schemes.Success status
        Theme.apply schemes.Canvas sortBackdrop
        Theme.apply schemes.Information sortFrame
        Theme.apply schemes.Canvas sortContents
        Theme.apply schemes.Canvas confirmationBackdrop
        Theme.apply schemes.Information confirmationFrame
        Theme.apply schemes.Canvas helpBackdrop
        Theme.apply schemes.Information helpFrame
        Theme.apply schemes.Canvas helpContents

        packageList.SetSource rows

        packageList.ViewportSettings <-
            packageList.ViewportSettings ||| ViewportSettingsFlags.HasVerticalScrollBar

        context.ViewportSettings <-
            context.ViewportSettings ||| ViewportSettingsFlags.HasVerticalScrollBar

        Theme.apply schemes.Muted packageList.VerticalScrollBar
        Theme.apply schemes.Muted context.VerticalScrollBar

        projectTable.FullRowSelect <- false
        projectTable.MultiSelect <- false
        projectTable.Style.ExpandLastColumn <- false
        projectTable.Style.InvertSelectedCellFirstCharacter <- false
        projectTable.Style.ShowHorizontalHeaderOverline <- false
        projectTable.Style.ShowHorizontalHeaderUnderline <- false
        projectTable.Style.ShowHorizontalBottomLine <- false
        projectTable.Style.ShowVerticalCellLines <- false
        projectTable.Style.ShowVerticalHeaderLines <- false
        projectTable.Style.ShowVerticalCellLineForFirstColumn <- false
        projectTable.Style.ShowVerticalCellLineForLastColumn <- false
        projectTable.Style.HeaderScheme <- passiveContentScheme

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
            if
                not rendering && projection.ListIsInteractive && not (backgroundInputBlocked ())
            then
                args.NewValue
                |> Option.ofNullable
                |> Option.bind (fun index -> model.Packages |> List.tryItem index)
                |> Option.iter (fun package -> dispatch (SelectPackage package.Id)))

        modeButtons
        |> List.iter (fun (mode, button) ->
            button.Accepting.Add(fun _ ->
                if not (backgroundInputBlocked ()) then
                    dispatch (ChangeMode mode)))

        detailsButton.Accepting.Add(fun _ ->
            if not (backgroundInputBlocked ()) then
                selectedPackage ()
                |> Option.filter (fun _ -> projection.ListIsInteractive)
                |> Option.iter (fun package -> dispatch (ShowDetails package.Id)))

        readmeButton.Accepting.Add(fun _ ->
            if not (backgroundInputBlocked ()) then
                selectedPackage ()
                |> Option.filter (fun _ -> projection.ListIsInteractive)
                |> Option.iter (fun package -> dispatch (ShowReadme package.Id)))

        previewButtons
        |> List.iter (fun (tab, button) ->
            button.Accepting.Add(fun _ ->
                if not (backgroundInputBlocked ()) then
                    dispatch (SelectPreviewTab tab)))

        source.Accepting.Add(fun _ ->
            if not (backgroundInputBlocked ()) then
                let value =
                    if String.IsNullOrWhiteSpace source.Text then
                        None
                    else
                        Some(PackageSource(source.Text.Trim()))

                dispatch (SelectSource value)
                dispatch SubmitSearch)

        search.Accepting.Add(fun _ ->
            if not (backgroundInputBlocked ()) then
                dispatch (ChangeSearch(search.Text, model.Query.IncludePrerelease))
                dispatch SubmitSearch)

        prerelease.ValueChanged.Add(fun args ->
            if not rendering && not (backgroundInputBlocked ()) then
                dispatch (ChangeSearch(search.Text, args.NewValue = CheckState.Checked))
                dispatch SubmitSearch)

        bindFallbackKeys this

        this.ViewportChanged.Add(fun _ ->
            let wasRendering = rendering
            rendering <- true
            projection <- Presentation.project this.Viewport.Width model
            listFrame.Title <- projection.ListTitle
            updateRows projection
            updateListState projection
            updateContextText this.Viewport.Width
            setLayout this.Viewport.Width
            rendering <- wasRendering)

        contextFrame.ViewportChanged.Add(fun _ ->
            setLayout this.Viewport.Width
            this.SetNeedsDraw())

        render initial

    member _.Render nextModel = render nextModel
    member _.BindKeyboard keyboard = bindKeyboard keyboard
