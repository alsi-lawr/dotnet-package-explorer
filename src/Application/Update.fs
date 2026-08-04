namespace Dotnet.PackageExplorer.Application

module Update =
    let private contentRoute route =
        match route with
        | Content content
        | Failure(content, _) -> content

    let private currentPreview route =
        match contentRoute route with
        | OperationPreview(preview, _)
        | OperationConfirmation preview -> Some preview
        | PackageList
        | PackageDetails _
        | PackageReadme _
        | PackageTargeting _
        | OperationProgress _ -> None

    let private currentOperationId route =
        match contentRoute route with
        | OperationPreview(preview, _)
        | OperationConfirmation preview -> Some preview.Id
        | OperationProgress progress -> Some progress.Preview
        | PackageList
        | PackageDetails _
        | PackageReadme _
        | PackageTargeting _ -> None

    let private allocateToken model =
        RequestToken model.NextToken,
        { model with
            NextToken = model.NextToken + 1L }

    let private showFailure scope failure model =
        { model with
            Route = Failure(contentRoute model.Route, scope)
            Failures = model.Failures.Add(scope, failure) }

    let private clearFailure scope model =
        let route =
            match model.Route with
            | Failure(retained, activeScope) when activeScope = scope -> Content retained
            | current -> current

        { model with
            Route = route
            Failures = model.Failures.Remove scope }

    let private unsupportedCapability scope capability =
        { Scope = scope
          Kind = Rejected $"The backend does not support {capability}."
          Message = $"The backend does not support {capability}." }

    let private requestScope requestKind model =
        match requestKind with
        | SearchRequest -> BackendSessionFailure
        | RefreshRequest -> BackendSessionFailure
        | UpdatesRequest -> BackendSessionFailure
        | ConsolidationRequest -> BackendSessionFailure
        | DetailsRequest
        | ReadmeRequest ->
            match contentRoute model.Route with
            | PackageDetails package
            | PackageReadme package
            | PackageTargeting package -> PackageFailure package
            | _ -> BackendSessionFailure
        | PreviewRequest
        | ApplyRequest -> currentOperationId model.Route |> OperationFailure

    let private pendingToken requestKind pending =
        match requestKind with
        | SearchRequest -> pending.Search
        | RefreshRequest -> pending.Refresh
        | UpdatesRequest -> pending.Updates
        | ConsolidationRequest -> pending.Consolidation
        | DetailsRequest -> pending.Details
        | ReadmeRequest -> pending.Readme
        | PreviewRequest -> pending.Preview
        | ApplyRequest -> pending.Apply

    let private clearPending requestKind (pending: PendingRequests) =
        match requestKind with
        | SearchRequest -> { pending with Search = None }
        | RefreshRequest -> { pending with Refresh = None }
        | UpdatesRequest -> { pending with Updates = None }
        | ConsolidationRequest -> { pending with Consolidation = None }
        | DetailsRequest -> { pending with Details = None }
        | ReadmeRequest -> { pending with Readme = None }
        | PreviewRequest -> { pending with Preview = None }
        | ApplyRequest -> { pending with Apply = None }

    let private setProject selected project projects =
        if selected then
            Set.add project projects
        else
            Set.remove project projects

    let private updateFramework selected project framework frameworks =
        let current = frameworks |> Map.tryFind project |> Option.defaultValue Set.empty
        let updated = setProject selected framework current

        if Set.isEmpty updated then
            frameworks.Remove project
        else
            frameworks.Add(project, updated)

    let private packageName (PackageId package) = package

    let private commonVersion fallback versions =
        match versions |> List.distinct with
        | [] -> fallback
        | [ version ] -> Some version
        | _ -> None

    let private installedSummary (model: Model) package =
        model.Installed
        |> Option.map InstalledSnapshot.packages
        |> Option.bind (List.tryFind (fun summary -> summary.Id = package))
        |> Option.defaultWith (fun () ->
            { Id = package
              DisplayName = packageName package
              InstalledVersion = None
              LatestVersion = None
              Kind = None
              Source = None
              Relevance = None
              Description = None })

    let private updatePackages (model: Model) (page: PackageUpdatesPage) =
        page.Updates
        |> List.groupBy _.Package
        |> List.map (fun (package, updates: PackageUpdate list) ->
            let summary = installedSummary model package

            { summary with
                InstalledVersion =
                    updates
                    |> List.choose _.InstalledVersion
                    |> commonVersion summary.InstalledVersion
                LatestVersion =
                    updates
                    |> List.collect _.AvailableVersions
                    |> List.distinct
                    |> List.tryHead
                    |> Option.orElse summary.LatestVersion })

    let private consolidationPackages (model: Model) (page: PackageConsolidationPage) =
        page.Packages
        |> List.map (fun (package: PackageConsolidation) ->
            let summary = installedSummary model package.Package

            { summary with
                InstalledVersion =
                    package.CurrentVersions
                    |> List.map fst
                    |> commonVersion summary.InstalledVersion
                LatestVersion =
                    package.CandidateVersions
                    |> List.tryHead
                    |> Option.orElse summary.LatestVersion })

    let private modePackages (model: Model) mode =
        match mode with
        | Browse -> []
        | Installed ->
            model.Installed
            |> Option.map InstalledSnapshot.packages
            |> Option.defaultValue []
        | Updates ->
            model.AvailableUpdates
            |> Option.map (updatePackages model)
            |> Option.defaultValue []
        | Consolidate ->
            model.AvailableConsolidation
            |> Option.map (consolidationPackages model)
            |> Option.defaultValue []

    let private modeHasNextPage (model: Model) mode =
        match mode with
        | Updates -> model.AvailableUpdates |> Option.bind _.Continuation |> Option.isSome
        | Consolidate -> model.AvailableConsolidation |> Option.bind _.Continuation |> Option.isSome
        | Browse
        | Installed -> false

    let private requestModeData (model: Model) =
        match model.Mode with
        | Browse when model.Capabilities.Contains BrowsePackages ->
            let token, next = allocateToken model

            { next with
                Pending.Search = Some token },
            [ SearchPackages
                  { Token = token
                    Target = model.Target
                    Source = model.SelectedSource
                    Query = model.Query } ]
        | Installed when model.Capabilities.Contains ReadInstalledPackages ->
            let token, next = allocateToken model

            { next with
                Pending.Refresh = Some token },
            [ RefreshInstalled { Token = token; Target = model.Target } ]
        | Updates when model.Capabilities.Contains UpdatePackages ->
            let token, next = allocateToken model

            { next with
                Pending =
                    { next.Pending with
                        Updates = Some token
                        Consolidation = None } },
            [ FindPackageUpdates
                  { Token = token
                    Target = model.Target
                    IncludePrerelease = model.Query.IncludePrerelease
                    PageSize = model.Query.PageSize
                    Continuation = None } ]
        | Consolidate when model.Capabilities.Contains ConsolidatePackages ->
            let token, next = allocateToken model

            { next with
                Pending =
                    { next.Pending with
                        Updates = None
                        Consolidation = Some token } },
            [ FindPackageConsolidation
                  { Token = token
                    Target = model.Target
                    PageSize = model.Query.PageSize
                    Continuation = None } ]
        | Browse
        | Installed
        | Updates
        | Consolidate -> model, []

    let private requestInstalledDependentModeData (model: Model) =
        match model.Mode with
        | Updates
        | Consolidate -> requestModeData model
        | Browse
        | Installed -> model, []

    let private projectSelectedVersion selectedVersions operation =
        let selected package current =
            selectedVersions |> Map.tryFind package |> Option.defaultValue current

        match operation with
        | InstallPackage(package, version) ->
            InstallPackage(
                package,
                selectedVersions |> Map.tryFind package |> Option.orElse version
            )
        | ConsolidatePackage(package, version) ->
            ConsolidatePackage(package, selected package version)
        | UpdateSelectedPackages _
        | UninstallPackage _ -> operation

    let update message model =
        match message with
        | ChangeMode mode ->
            let required = Capability.requiredForMode mode

            if model.Capabilities.Contains required then
                let sort = PackageSort.defaultForMode mode

                let next =
                    { model with
                        Mode = mode
                        Sort = sort
                        HasNextPage = modeHasNextPage model mode
                        Packages = modePackages model mode |> PackageSort.apply mode sort
                        ActivePackage = None
                        SelectedPackages = Set.empty
                        Route = Content PackageList
                        Pending =
                            { model.Pending with
                                Search = None
                                Updates = None
                                Consolidation = None
                                Details = None
                                Readme = None
                                Preview = None
                                Apply = None } }

                requestModeData next
            else
                showFailure
                    BackendSessionFailure
                    (unsupportedCapability BackendSessionFailure required)
                    model,
                []

        | ChangeTarget(target, installed) ->
            let packages =
                match model.Mode with
                | Browse -> []
                | Installed ->
                    installed |> Option.map InstalledSnapshot.packages |> Option.defaultValue []
                | Updates
                | Consolidate -> []

            let next =
                { model with
                    Target = target
                    Installed = installed
                    AvailableUpdates = None
                    AvailableConsolidation = None
                    HasNextPage = false
                    Packages = PackageSort.apply model.Mode model.Sort packages
                    TargetSelection = TargetSelection.forTarget target
                    ActivePackage = None
                    SelectedPackages = Set.empty
                    Route = Content PackageList
                    Pending = PendingRequests.empty }

            if model.Capabilities.Contains ReadInstalledPackages then
                let token, allocated = allocateToken next

                { allocated with
                    Pending.Refresh = Some token },
                [ RefreshInstalled { Token = token; Target = target } ]
            else
                next, []

        | ChangeSort sort ->
            { model with
                Sort = sort
                Packages = PackageSort.apply model.Mode sort model.Packages },
            []

        | ChangeSearch(text, includePrerelease) ->
            { model with
                Query =
                    { model.Query with
                        Text = text
                        IncludePrerelease = includePrerelease
                        Page = 0 }
                HasNextPage = false
                Pending.Search = None },
            []

        | SelectSource source ->
            { model with
                SelectedSource = source
                HasNextPage = false
                Pending.Search = None },
            []

        | SelectVersion(package, version) ->
            let versions =
                match version with
                | Some selected -> model.SelectedVersions.Add(package, selected)
                | None -> model.SelectedVersions.Remove package

            { model with
                SelectedVersions = versions
                Pending =
                    { model.Pending with
                        Preview = None
                        Apply = None } },
            []

        | ChangePage page ->
            { model with
                Query.Page = max 0 page
                HasNextPage = false
                Pending.Search = None },
            []

        | SubmitSearch when model.Mode = Updates -> requestModeData model
        | SubmitSearch when model.Mode = Consolidate -> requestModeData model
        | SubmitSearch when model.Mode = Installed -> model, []
        | SubmitSearch when not (model.Capabilities.Contains BrowsePackages) ->
            showFailure
                BackendSessionFailure
                (unsupportedCapability BackendSessionFailure BrowsePackages)
                model,
            []

        | SubmitSearch ->
            let token, next = allocateToken model

            { next with
                HasNextPage = false
                Pending.Search = Some token },
            [ SearchPackages
                  { Token = token
                    Target = model.Target
                    Source = model.SelectedSource
                    Query = model.Query } ]

        | SearchCompleted(token, _) when model.Pending.Search <> Some token -> model, []
        | SearchCompleted(_, Ok page) ->
            let next =
                { model with
                    Query = page.Query
                    HasNextPage = page.HasNextPage
                    Packages = PackageSort.apply model.Mode model.Sort page.Packages
                    Pending.Search = None }
                |> clearFailure BackendSessionFailure

            next, []
        | SearchCompleted(_, Error failure) ->
            { model with
                HasNextPage = false
                Pending.Search = None }
            |> showFailure failure.Scope failure,
            []

        | Refresh when not (model.Capabilities.Contains ReadInstalledPackages) ->
            showFailure
                BackendSessionFailure
                (unsupportedCapability BackendSessionFailure ReadInstalledPackages)
                model,
            []

        | Refresh ->
            let token, next = allocateToken model

            { next with
                Pending.Refresh = Some token },
            [ RefreshInstalled { Token = token; Target = model.Target } ]

        | RefreshCompleted(token, _) when model.Pending.Refresh <> Some token -> model, []
        | RefreshCompleted(_, Ok snapshot) ->
            let packages =
                match model.Mode with
                | Browse -> model.Packages
                | Installed ->
                    snapshot
                    |> InstalledSnapshot.packages
                    |> PackageSort.apply model.Mode model.Sort
                | Updates
                | Consolidate -> model.Packages

            let next =
                { model with
                    Installed = Some snapshot
                    Packages = packages
                    Pending.Refresh = None }
                |> clearFailure BackendSessionFailure

            requestInstalledDependentModeData next
        | RefreshCompleted(_, Error failure) ->
            { model with Pending.Refresh = None } |> showFailure failure.Scope failure, []

        | UpdatesCompleted(token, _) when model.Pending.Updates <> Some token -> model, []
        | UpdatesCompleted(_, Ok page) ->
            { model with
                AvailableUpdates = Some page
                HasNextPage = page.Continuation.IsSome
                Packages = page |> updatePackages model |> PackageSort.apply Updates model.Sort
                Pending.Updates = None }
            |> clearFailure BackendSessionFailure,
            []
        | UpdatesCompleted(_, Error failure) ->
            { model with Pending.Updates = None } |> showFailure failure.Scope failure, []

        | ConsolidationCompleted(token, _) when model.Pending.Consolidation <> Some token ->
            model, []
        | ConsolidationCompleted(_, Ok page) ->
            { model with
                AvailableConsolidation = Some page
                HasNextPage = page.Continuation.IsSome
                Packages =
                    page |> consolidationPackages model |> PackageSort.apply Consolidate model.Sort
                Pending.Consolidation = None }
            |> clearFailure BackendSessionFailure,
            []
        | ConsolidationCompleted(_, Error failure) ->
            { model with
                Pending.Consolidation = None }
            |> showFailure failure.Scope failure,
            []

        | SelectPackage package ->
            { model with
                ActivePackage = Some package
                Focus = PackageRow package },
            []

        | SetPackageSelection(package, selected) ->
            { model with
                SelectedPackages = setProject selected package model.SelectedPackages },
            []

        | ShowDetails package when not (model.Capabilities.Contains ReadPackageDetails) ->
            showFailure
                (PackageFailure package)
                (unsupportedCapability (PackageFailure package) ReadPackageDetails)
                model,
            []

        | ShowDetails package ->
            let token, next = allocateToken model

            { next with
                Route = Content(PackageDetails package)
                Pending =
                    { next.Pending with
                        Details = Some token
                        Readme = None } },
            [ GetPackageDetails
                  { Token = token
                    Target = model.Target
                    Package = package } ]

        | DetailsCompleted(token, _, _) when model.Pending.Details <> Some token -> model, []
        | DetailsCompleted(_, package, Ok details) ->
            { model with
                Details = model.Details.Add(package, details)
                Route = Content(PackageDetails package)
                Pending.Details = None }
            |> clearFailure (PackageFailure package),
            []
        | DetailsCompleted(_, _, Error failure) ->
            { model with Pending.Details = None } |> showFailure failure.Scope failure, []

        | ShowReadme package when not (model.Capabilities.Contains ReadPackageReadme) ->
            showFailure
                (PackageFailure package)
                (unsupportedCapability (PackageFailure package) ReadPackageReadme)
                model,
            []

        | ShowReadme package ->
            let token, next = allocateToken model

            { next with
                Route = Content(PackageReadme package)
                Pending =
                    { next.Pending with
                        Details = None
                        Readme = Some token } },
            [ GetPackageReadme
                  { Token = token
                    Target = model.Target
                    Package = package } ]

        | ReadmeCompleted(token, _, _) when model.Pending.Readme <> Some token -> model, []
        | ReadmeCompleted(_, package, Ok readme) ->
            { model with
                Readmes = model.Readmes.Add(package, readme)
                Route = Content(PackageReadme package)
                Pending.Readme = None }
            |> clearFailure (PackageFailure package),
            []
        | ReadmeCompleted(_, _, Error failure) ->
            { model with Pending.Readme = None } |> showFailure failure.Scope failure, []

        | ShowTargeting package when WorkspaceTarget.supportsProjectSelection model.Target ->
            { model with
                Route = Content(PackageTargeting package)
                Pending =
                    { model.Pending with
                        Details = None
                        Readme = None } },
            []
        | ShowTargeting _ -> model, []

        | SetProjectSelection(project, selected) when
            WorkspaceTarget.supportsProjectSelection model.Target
            ->
            let projects = setProject selected project model.TargetSelection.Projects

            let frameworks =
                if selected then
                    model.TargetSelection.Frameworks
                else
                    model.TargetSelection.Frameworks.Remove project

            { model with
                TargetSelection =
                    { Projects = projects
                      Frameworks = frameworks } },
            []
        | SetProjectSelection _ -> model, []

        | SetFrameworkSelection(project, framework, selected) when
            WorkspaceTarget.supportsProjectSelection model.Target
            && model.TargetSelection.Projects.Contains project
            ->
            let selectedFrameworks =
                updateFramework selected project framework model.TargetSelection.Frameworks

            let selection =
                { model.TargetSelection with
                    Frameworks = selectedFrameworks }

            { model with
                TargetSelection = selection },
            []
        | SetFrameworkSelection _ -> model, []

        | RequestPreview _ when not (model.Capabilities.Contains PreviewOperations) ->
            showFailure
                (OperationFailure None)
                (unsupportedCapability (OperationFailure None) PreviewOperations)
                model,
            []
        | RequestPreview(UpdateSelectedPackages packages) when Set.isEmpty packages ->
            let failure =
                { Scope = OperationFailure None
                  Kind = Rejected "Select one or more packages to update."
                  Message = "Select one or more packages to update." }

            showFailure (OperationFailure None) failure model, []
        | RequestPreview operation ->
            let token, next = allocateToken model
            let projectedOperation = projectSelectedVersion model.SelectedVersions operation

            { next with
                Pending =
                    { next.Pending with
                        Details = None
                        Readme = None
                        Preview = Some token
                        Apply = None } },
            [ PreviewOperation
                  { Token = token
                    Target = model.Target
                    Selection = model.TargetSelection
                    Operation = projectedOperation } ]

        | PreviewCompleted(token, _) when model.Pending.Preview <> Some token -> model, []
        | PreviewCompleted(_, Ok preview) ->
            { model with
                Route = Content(OperationPreview(preview, Summary))
                Pending.Preview = None }
            |> clearFailure (OperationFailure None),
            []
        | PreviewCompleted(_, Error failure) ->
            { model with Pending.Preview = None } |> showFailure failure.Scope failure, []

        | SelectPreviewTab tab ->
            match contentRoute model.Route with
            | OperationPreview(preview, _) ->
                { model with
                    Route = Content(OperationPreview(preview, tab)) },
                []
            | _ -> model, []

        | ConfirmPreview previewId when not (model.Capabilities.Contains ApplyOperations) ->
            showFailure
                (OperationFailure(Some previewId))
                (unsupportedCapability (OperationFailure(Some previewId)) ApplyOperations)
                model,
            []
        | ConfirmPreview previewId ->
            match currentPreview model.Route with
            | Some preview when preview.Id = previewId ->
                let token, next = allocateToken model

                { next with
                    Route = Content(OperationConfirmation preview)
                    Pending.Apply = Some token },
                [ ApplyOperation
                      { Token = token
                        Target = model.Target
                        Preview = previewId } ]
            | _ -> model, []

        | ApplyProgressed(token, _) when model.Pending.Apply <> Some token -> model, []
        | ApplyProgressed(_, progress) ->
            { model with
                Route = Content(OperationProgress progress) },
            []

        | ApplyCompleted(token, _) when model.Pending.Apply <> Some token -> model, []
        | ApplyCompleted(_, Ok result) ->
            let packages =
                match model.Mode with
                | Browse -> model.Packages
                | Installed ->
                    result.Installed
                    |> InstalledSnapshot.packages
                    |> PackageSort.apply model.Mode model.Sort
                | Updates
                | Consolidate -> model.Packages

            let next =
                { model with
                    Installed = Some result.Installed
                    Packages = packages
                    Route = Content PackageList
                    Pending.Apply = None }
                |> clearFailure (OperationFailure(Some result.Preview))

            requestInstalledDependentModeData next
        | ApplyCompleted(_, Error failure) ->
            { model with Pending.Apply = None } |> showFailure failure.Scope failure, []

        | Cancel requestKind ->
            match pendingToken requestKind model.Pending with
            | None -> model, []
            | Some token ->
                let failure =
                    { Scope = requestScope requestKind model
                      Kind = Cancelled
                      Message = "The request was cancelled." }

                let next =
                    { model with
                        Pending = clearPending requestKind model.Pending }

                let next =
                    if requestKind = SearchRequest then
                        { next with HasNextPage = false }
                    else
                        next

                next |> showFailure failure.Scope failure, [ CancelRequest token ]

        | BackendSessionFailed failure ->
            { model with
                HasNextPage = false
                Pending = PendingRequests.empty }
            |> showFailure failure.Scope failure,
            []

        | DismissFailure scope ->
            let route =
                match model.Route with
                | Failure(retained, activeScope) when activeScope = scope -> Content retained
                | current -> current

            { model with
                Failures = model.Failures.Remove scope
                Route = route },
            []

        | SetFocus focus -> { model with Focus = focus }, []
