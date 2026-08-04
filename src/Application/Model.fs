namespace Dotnet.PackageExplorer.Application

type ContentRoute =
    | PackageList
    | PackageDetails of PackageId
    | PackageReadme of PackageId
    | PackageTargeting of PackageId
    | OperationPreview of OperationPreview * PreviewTab
    | OperationConfirmation of OperationPreview
    | OperationProgress of OperationProgress

type Route =
    | Content of ContentRoute
    | Failure of retained: ContentRoute * scope: FailureScope

type FocusIdentity =
    | ModeTabs
    | PackageSearch
    | PackageRow of PackageId
    | DetailsPane
    | ProjectRow of ProjectId
    | PreviewPane

type PendingRequests =
    { Search: RequestToken option
      Refresh: RequestToken option
      Updates: RequestToken option
      Consolidation: RequestToken option
      Details: RequestToken option
      Readme: RequestToken option
      Preview: RequestToken option
      Apply: RequestToken option }

module PendingRequests =
    let empty =
        { Search = None
          Refresh = None
          Updates = None
          Consolidation = None
          Details = None
          Readme = None
          Preview = None
          Apply = None }

type Model =
    { Mode: PackageMode
      Target: WorkspaceTarget
      Capabilities: Set<Capability>
      Query: SearchQuery
      HasNextPage: bool
      Packages: PackageSummary list
      Installed: InstalledSnapshot option
      AvailableUpdates: PackageUpdatesPage option
      AvailableConsolidation: PackageConsolidationPage option
      Sort: PackageSort
      SelectedSource: PackageSource option
      SelectedVersions: Map<PackageId, PackageVersion>
      ActivePackage: PackageId option
      SelectedPackages: Set<PackageId>
      TargetSelection: TargetSelection
      Details: Map<PackageId, PackageDetails>
      Readmes: Map<PackageId, PackageReadme>
      Route: Route
      Focus: FocusIdentity
      Pending: PendingRequests
      Failures: Map<FailureScope, ApplicationFailure>
      NextToken: int64 }

type Effect =
    | SearchPackages of SearchPackagesRequest
    | RefreshInstalled of InstalledRefreshRequest
    | FindPackageUpdates of PackageUpdatesRequest
    | FindPackageConsolidation of PackageConsolidationRequest
    | GetPackageDetails of PackageDetailsRequest
    | GetPackageReadme of PackageReadmeRequest
    | PreviewOperation of PreviewOperationRequest
    | ApplyOperation of ApplyOperationRequest
    | CancelRequest of RequestToken

type Message =
    | ChangeMode of PackageMode
    | ChangeTarget of WorkspaceTarget * InstalledSnapshot option
    | ChangeSort of PackageSort
    | ChangeSearch of text: string * includePrerelease: bool
    | SelectSource of PackageSource option
    | SelectVersion of PackageId * PackageVersion option
    | ChangePage of int
    | SubmitSearch
    | SearchCompleted of RequestToken * Result<SearchPage, ApplicationFailure>
    | Refresh
    | RefreshCompleted of RequestToken * Result<InstalledSnapshot, ApplicationFailure>
    | UpdatesCompleted of RequestToken * Result<PackageUpdatesPage, ApplicationFailure>
    | ConsolidationCompleted of RequestToken * Result<PackageConsolidationPage, ApplicationFailure>
    | SelectPackage of PackageId
    | SetPackageSelection of PackageId * selected: bool
    | ShowDetails of PackageId
    | DetailsCompleted of RequestToken * PackageId * Result<PackageDetails, ApplicationFailure>
    | ShowReadme of PackageId
    | ReadmeCompleted of RequestToken * PackageId * Result<PackageReadme, ApplicationFailure>
    | ShowTargeting of PackageId
    | SetProjectSelection of ProjectId * selected: bool
    | SetFrameworkSelection of ProjectId * TargetFramework * selected: bool
    | RequestPreview of PackageOperation
    | PreviewCompleted of RequestToken * Result<OperationPreview, ApplicationFailure>
    | SelectPreviewTab of PreviewTab
    | ConfirmPreview of PreviewId
    | ApplyProgressed of RequestToken * OperationProgress
    | ApplyCompleted of RequestToken * Result<OperationResult, ApplicationFailure>
    | Cancel of RequestKind
    | BackendSessionFailed of ApplicationFailure
    | DismissFailure of FailureScope
    | SetFocus of FocusIdentity

module Model =
    let allCapabilities =
        set
            [ BrowsePackages
              ReadInstalledPackages
              UpdatePackages
              ConsolidatePackages
              ReadPackageDetails
              ReadPackageReadme
              PreviewOperations
              ApplyOperations ]

    let create
        (target: WorkspaceTarget)
        (capabilities: Set<Capability>)
        (installed: InstalledSnapshot option)
        =
        let mode = Installed

        let packages =
            installed |> Option.map InstalledSnapshot.packages |> Option.defaultValue []

        let model =
            { Mode = mode
              Target = target
              Capabilities = capabilities
              Query =
                { Text = ""
                  IncludePrerelease = false
                  Page = 0
                  PageSize = 50 }
              HasNextPage = false
              Packages = PackageSort.apply mode (PackageSort.defaultForMode mode) packages
              Installed = installed
              AvailableUpdates = None
              AvailableConsolidation = None
              Sort = PackageSort.defaultForMode mode
              SelectedSource = None
              SelectedVersions = Map.empty
              ActivePackage = None
              SelectedPackages = Set.empty
              TargetSelection = TargetSelection.forTarget target
              Details = Map.empty
              Readmes = Map.empty
              Route = Content PackageList
              Focus = ModeTabs
              Pending = PendingRequests.empty
              Failures = Map.empty
              NextToken = 1L }

        if capabilities.Contains ReadInstalledPackages then
            let token = RequestToken model.NextToken

            { model with
                Pending.Refresh = Some token
                NextToken = model.NextToken + 1L },
            [ RefreshInstalled { Token = token; Target = target } ]
        else
            model, []
