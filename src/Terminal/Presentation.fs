namespace Dotnet.PackageExplorer.Terminal

open System
open Dotnet.PackageExplorer.Application

type TerminalWidth =
    | Wide
    | Narrow

type TerminalProjection =
    { Modes: string list
      Search: string
      ListTitle: string
      ListHeading: string
      ListNotice: string option
      ListIsInteractive: bool
      Rows: string list
      ContextTitle: string
      Context: string
      Actions: string
      Status: string
      Route: string
      Width: TerminalWidth }

[<RequireQualifiedAccess>]
module internal Presentation =
    [<Literal>]
    let NarrowBoundary = 100

    let width columns =
        if columns < NarrowBoundary then Narrow else Wide

    let packageId (PackageId value) = value

    let packageVersion =
        function
        | Some(PackageVersion value) -> value
        | None -> "-"

    let private packageKind =
        function
        | Some Direct -> "Direct"
        | Some Transitive -> "Transitive"
        | Some Central -> "Central"
        | Some Framework -> "Framework"
        | None -> "Available"

    let private fit width (value: string) =
        if width <= 0 then ""
        elif value.Length <= width then value
        elif width <= 2 then value[.. width - 1]
        else value[.. width - 3] + ".."

    let private pad width value = (fit width value).PadRight width

    let private modeName =
        function
        | Browse -> "Browse"
        | Installed -> "Installed"
        | Updates -> "Updates"
        | Consolidate -> "Consolidate"

    let private sortName sort =
        let field =
            match sort.Field with
            | Relevance -> "Relevance"
            | Name -> "Package"
            | Version -> "Version"
            | Type -> "Type"

        let direction =
            match sort.Direction with
            | Ascending -> "ascending"
            | Descending -> "descending"

        $"{field}, {direction}"

    let private modeTitle (model: Model) =
        match model.Mode with
        | Browse -> $"Packages | {model.Packages.Length} results"
        | Installed -> "Installed packages"
        | Updates -> "Available updates"
        | Consolidate -> "Version differences"

    let private listContentWidth columns =
        match width columns with
        | Narrow -> max 20 (columns - 4)
        | Wide -> max 20 (columns * 45 / 100 - 5)

    let private listPending (model: Model) =
        if model.Pending.Preview.IsSome then
            Some "Building preview"
        elif model.Pending.Refresh.IsSome then
            Some "Refreshing installed packages"
        else
            match model.Mode with
            | Browse when model.Pending.Search.IsSome -> Some "Searching packages"
            | Updates when model.Pending.Updates.IsSome -> Some "Finding package updates"
            | Consolidate when model.Pending.Consolidation.IsSome ->
                Some "Finding version differences"
            | Browse
            | Installed
            | Updates
            | Consolidate -> None

    let private listNotice (model: Model) =
        match listPending model, model.Packages with
        | Some label, [] -> Some $"{label}..."
        | Some label, _ -> Some $"{label}... ~ marks previous results; package actions are paused."
        | None, [] ->
            match model.Mode with
            | Browse -> Some "No packages found. Press / to change the search or filters."
            | Installed -> Some "No installed packages. Press 1 to browse packages."
            | Updates -> Some "No updates found. Press / to change filters or r to refresh."
            | Consolidate ->
                Some "No version differences found. Press / to change filters or r to refresh."
        | None, _ -> None

    let private sourceName (package: PackageSummary) =
        package.Source
        |> Option.map (fun (PackageSource value) -> value)
        |> Option.defaultValue "unknown"

    let private maxLength fallback maximum values =
        values |> List.map String.length |> List.fold max fallback |> min maximum

    type private RowLayout =
        { Name: int
          Current: int
          Latest: int
          Tail: int }

    let private rowLayout contentWidth (model: Model) =
        let currentWidth =
            model.Packages
            |> List.map (_.InstalledVersion >> packageVersion)
            |> maxLength 7 16

        let latestWidth =
            model.Packages |> List.map (_.LatestVersion >> packageVersion) |> maxLength 7 16

        let tailWidth =
            match model.Mode with
            | Browse -> model.Packages |> List.map sourceName |> maxLength 6 16
            | Installed
            | Updates
            | Consolidate -> model.Packages |> List.map (_.Kind >> packageKind) |> maxLength 6 10

        let prefixWidth =
            match model.Mode with
            | Browse
            | Installed -> 2
            | Updates
            | Consolidate -> 6

        let fixedWidth =
            match model.Mode with
            | Browse -> prefixWidth + 1 + latestWidth + 1 + tailWidth
            | Installed -> prefixWidth + 1 + currentWidth + 1 + tailWidth
            | Updates
            | Consolidate -> prefixWidth + 1 + currentWidth + 4 + latestWidth + 1 + tailWidth

        { Name = max 8 (contentWidth - fixedWidth)
          Current = currentWidth
          Latest = latestWidth
          Tail = tailWidth }

    let private rowHeading layout (model: Model) =
        match model.Mode with
        | Browse ->
            "  "
            + pad layout.Name "Package"
            + " "
            + pad layout.Latest "Version"
            + " "
            + fit layout.Tail "Source"
        | Installed ->
            "  " + pad layout.Name "Package" + " " + pad layout.Current "Version" + " Kind"
        | Updates
        | Consolidate ->
            "  Sel "
            + pad layout.Name "Package"
            + " "
            + pad layout.Current "Current"
            + " -> "
            + pad layout.Latest "Latest"
            + " Kind"

    let private row layout isPending (model: Model) (package: PackageSummary) =
        let selected =
            if model.SelectedPackages.Contains package.Id then
                "[x]"
            else
                "[ ]"

        let marker =
            match model.Mode with
            | Browse
            | Installed -> ""
            | Updates
            | Consolidate -> selected

        let focus =
            if model.ActivePackage = Some package.Id then ">"
            elif isPending then "~"
            else " "

        let stale = if isPending then "~" else " "

        let current = packageVersion package.InstalledVersion
        let latest = packageVersion package.LatestVersion
        let name = package.DisplayName

        match model.Mode with
        | Browse ->
            focus
            + stale
            + pad layout.Name name
            + " "
            + pad layout.Latest latest
            + " "
            + fit layout.Tail (sourceName package)
        | Installed ->
            focus
            + stale
            + pad layout.Name name
            + " "
            + pad layout.Current current
            + " "
            + $"{packageKind package.Kind}"
        | Updates ->
            focus
            + stale
            + marker
            + " "
            + pad layout.Name name
            + " "
            + pad layout.Current current
            + " -> "
            + pad layout.Latest latest
            + " "
            + $"{packageKind package.Kind}"
        | Consolidate ->
            focus
            + stale
            + marker
            + " "
            + pad layout.Name name
            + " "
            + pad layout.Current current
            + " -> "
            + pad layout.Latest latest
            + " "
            + $"{packageKind package.Kind}"

    let private targetName (target: WorkspaceTarget) =
        match target with
        | SingleProject project -> project.Name
        | Solution(path, _)
        | Workspace(path, _) -> IO.Path.GetFileName path |> Option.ofObj |> Option.defaultValue path

    let private operationPackageCount =
        function
        | InstallPackage _
        | UninstallPackage _
        | ConsolidatePackage _ -> 1
        | UpdateSelectedPackages packages -> packages.Count

    let private packageCountText count =
        if count = 1 then "1 package" else $"{count} packages"

    let private progressIndicator percentage =
        let width = 20
        let completed = Math.Clamp(percentage, 0, 100) * width / 100
        "[" + String('#', completed) + String('.', width - completed) + "]"

    let private packageActions =
        "Tab modes | 1-4 jump | j/k rows | h/l tabs | C-h/C-l panes | s sort"
        + "\n/ search | Space select | Enter details | p preview | r refresh | "
        + "Esc back | q quit | ? Help"

    let private ownsFailureInput =
        function
        | BackendSessionFailure
        | OperationFailure _ -> true
        | SourceFailure _
        | PackageFailure _
        | ProjectFailure _ -> false

    let private detailsMarkdown (model: Model) (package: PackageId) =
        match Map.tryFind package model.Details with
        | None -> "Loading package details..."
        | Some details ->
            let summary =
                details.Package.Description
                |> Option.defaultValue "No description is available."

            let versions =
                details.Versions
                |> List.map (fun version -> $"- {packageVersion (Some version)}")
                |> String.concat Environment.NewLine

            let dependencies =
                details.Dependencies
                |> List.map (fun dependency ->
                    $"- {packageId dependency.Id} {dependency.VersionRange}")
                |> String.concat Environment.NewLine

            let deprecated =
                if details.IsDeprecated then
                    "**Deprecated**"
                else
                    "Supported"

            $"""# {details.Package.DisplayName}

{summary}

**State:** {packageKind details.Package.Kind}

**Package status:** {deprecated}

## Versions

{versions}

## Dependencies

{dependencies}"""

    let private readmeMarkdown (model: Model) (package: PackageId) =
        match Map.tryFind package model.Readmes with
        | Some readme -> readme.CommonMark
        | None -> "Loading package README..."

    let private targetingMarkdown (model: Model) (package: PackageId) =
        let selected (project: ProjectTarget) =
            if model.TargetSelection.Projects.Contains project.Id then
                "[x]"
            else
                "[ ]"

        let focused (project: ProjectTarget) =
            match model.Focus with
            | ProjectRow projectId when projectId = project.Id -> ">"
            | _ -> " "

        let projects =
            model.Target
            |> WorkspaceTarget.projects
            |> List.collect (fun project ->
                let frameworks =
                    match project.Frameworks with
                    | [] -> [ "(default)" ]
                    | values -> values |> List.map (fun (TargetFramework framework) -> framework)

                frameworks
                |> List.map (fun framework ->
                    $"| {focused project} {selected project} {project.Name} | {framework} |"))
            |> String.concat Environment.NewLine

        $"""# {packageId package}

## Projects and frameworks

| Project | Framework |
| --- | --- |
{projects}

Use j/k to choose a project. Space selects it and its frameworks. Press p to preview."""

    let private previewMarkdown (preview: OperationPreview) tab =
        match tab with
        | Summary ->
            preview.Summary
            |> List.map (fun line -> $"- {line}")
            |> String.concat Environment.NewLine
            |> fun value -> "# Summary\n\n" + value
        | Projects ->
            let projects =
                preview.Projects
                |> List.map (fun project ->
                    let (ProjectId name) = project.Project

                    let framework =
                        project.Framework
                        |> Option.map (fun (TargetFramework value) -> value)
                        |> Option.defaultValue "(all)"

                    $"| {name} | {framework} | {packageVersion project.Before} | "
                    + $"{packageVersion project.After} |")
                |> String.concat Environment.NewLine

            $"""# Project and framework impact

| Project | Framework | Current | Proposed |
| --- | --- | --- | --- |
{projects}"""
        | Dependencies ->
            preview.Dependencies
            |> List.map (fun dependency -> $"- {packageId dependency.Id} {dependency.VersionRange}")
            |> String.concat Environment.NewLine
            |> fun value -> "# Dependency impact\n\n" + value
        | Files ->
            preview.Files
            |> List.map (fun path -> $"- `{path}`")
            |> String.concat Environment.NewLine
            |> fun value -> "# File impact\n\n" + value

    let private content (model: Model) =
        let contentRoute, failure =
            match model.Route with
            | Content route -> route, None
            | Failure(route, scope) -> route, Map.tryFind scope model.Failures

        match failure with
        | Some problem when problem.Scope = BackendSessionFailure ->
            "Workspace Explorer | Packages",
            $"Workspace Explorer connection failed\n\n{problem.Message}\n\n"
            + "Press Esc to dismiss this message.",
            "Esc dismiss | ? Help",
            "Packages / Failure"
        | Some problem when ownsFailureInput problem.Scope ->
            "Workspace Explorer | Packages",
            $"Package operation failed\n\n{problem.Message}\n\n"
            + "Press Esc to dismiss this message.",
            "Esc dismiss | ? Help",
            "Packages / Failure"
        | Some problem -> "Package Explorer", problem.Message, packageActions, "Failure"
        | None ->
            match contentRoute with
            | PackageList ->
                match model.ActivePackage with
                | Some package ->
                    packageId package, detailsMarkdown model package, packageActions, "List"
                | None when List.isEmpty model.Packages ->
                    "Package details",
                    "Package information will appear here when results are available.",
                    packageActions,
                    "List"
                | None ->
                    "Package details",
                    "Select a package and press Enter to open its details.",
                    packageActions,
                    "List"
            | PackageDetails package ->
                packageId package, detailsMarkdown model package, packageActions, "Details"
            | PackageReadme package ->
                packageId package + " | README",
                readmeMarkdown model package,
                packageActions,
                "README"
            | PackageTargeting package ->
                packageId package + " | Targets",
                targetingMarkdown model package,
                packageActions,
                "Targets"
            | OperationPreview(preview, tab) ->
                let tabName =
                    match tab with
                    | Summary -> "Summary"
                    | Projects -> "Projects"
                    | Dependencies -> "Dependencies"
                    | Files -> "Files"

                "Preview | " + tabName,
                previewMarkdown preview tab,
                packageActions,
                "Preview / " + tabName
            | OperationConfirmation preview ->
                let count = operationPackageCount preview.Operation
                let packages = packageCountText count

                "Applying package changes",
                $"Applying changes for {packages}.\n\n"
                + "The approved preview is being applied.\n\nWaiting for progress...",
                "? Help",
                "Packages / Applying"
            | OperationProgress progress ->
                let total = max 1 progress.Total
                let completed = Math.Clamp(progress.Completed, 0, total)
                let percentage = (completed * 100) / total

                "Applying package changes",
                $"Current step: {progress.Status}\n\n"
                + $"{progressIndicator percentage} {percentage}"
                + "%\n\n"
                + $"Completed {completed} of {progress.Total} steps.",
                "? Help",
                "Packages / Progress"

    let ownsInput (model: Model) =
        match model.Route with
        | Failure(_, scope) -> ownsFailureInput scope
        | Content(OperationConfirmation _)
        | Content(OperationProgress _) -> true
        | Content _ -> false

    let project columns (model: Model) =
        let contextTitle, context, actions, route = content model
        let active = modeName model.Mode
        let contentWidth = listContentWidth columns
        let layout = rowLayout contentWidth model
        let isPending = listPending model |> Option.isSome
        let notice = listNotice model

        let modes =
            [ Browse; Installed; Updates; Consolidate ]
            |> List.map (fun mode ->
                let title = modeName mode

                if title = active then $"[{title}]" else title)

        let source =
            model.SelectedSource
            |> Option.map (fun (PackageSource value) -> value)
            |> Option.defaultValue "all sources"

        let status =
            if
                match model.Route with
                | Failure _ -> true
                | _ -> false
            then
                "Action required"
            elif model.Pending.Apply.IsSome then
                "Applying package changes..."
            elif model.Pending.Preview.IsSome then
                "Building preview..."
            elif model.Pending.Refresh.IsSome then
                "Refreshing installed packages..."
            elif model.Pending.Updates.IsSome then
                "Finding package updates..."
            elif model.Pending.Consolidation.IsSome then
                "Finding version differences..."
            elif model.Pending.Search.IsSome then
                "Searching packages..."
            else
                "Ready"

        { Modes = modes
          Search = $"/ {model.Query.Text} | {source}"
          ListTitle = $"{modeTitle model} | Sort: {sortName model.Sort} [s]"
          ListHeading = rowHeading layout model
          ListNotice = notice
          ListIsInteractive = notice.IsNone
          Rows = model.Packages |> List.map (row layout isPending model)
          ContextTitle = contextTitle
          Context = context
          Actions = actions
          Status = $"{targetName model.Target} | {status}"
          Route = route
          Width = width columns }
