namespace Dotnet.PackageExplorer.Application

type SearchPackagesRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      Source: PackageSource option
      Query: SearchQuery }

type PackageSourcesRequest =
    { Token: RequestToken
      Target: WorkspaceTarget }

type PackageSourceMappingRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      Package: PackageId
      Source: PackageSource option
      RestoredTransitives: PackageId list option }

type InstalledRefreshRequest =
    { Token: RequestToken
      Target: WorkspaceTarget }

type PackageUpdatesRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      IncludePrerelease: bool
      PageSize: int
      Continuation: string option }

type PackageConsolidationRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      PageSize: int
      Continuation: string option }

type PackageDetailsRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      Package: PackageId }

type PackageReadmeRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      Package: PackageId }

type PreviewOperationRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      Selection: TargetSelection
      Operation: PackageOperation }

type ApplyOperationRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      Preview: PreviewId }

type PackageExplorerClient =
    { Sources: PackageSourcesRequest -> Async<Result<PackageSourceInfo list, ApplicationFailure>>
      SourceMapping:
          PackageSourceMappingRequest -> Async<Result<PackageSourceMapping, ApplicationFailure>>
      Search: SearchPackagesRequest -> Async<Result<SearchPage, ApplicationFailure>>
      RefreshInstalled:
          InstalledRefreshRequest -> Async<Result<InstalledSnapshot, ApplicationFailure>>
      FindUpdates: PackageUpdatesRequest -> Async<Result<PackageUpdatesPage, ApplicationFailure>>
      FindConsolidation:
          PackageConsolidationRequest -> Async<Result<PackageConsolidationPage, ApplicationFailure>>
      GetDetails: PackageDetailsRequest -> Async<Result<PackageDetails, ApplicationFailure>>
      GetReadme: PackageReadmeRequest -> Async<Result<PackageReadme, ApplicationFailure>>
      Preview: PreviewOperationRequest -> Async<Result<OperationPreview, ApplicationFailure>>
      Apply: ApplyOperationRequest -> Async<Result<OperationResult, ApplicationFailure>>
      Cancel: RequestToken -> Async<Result<unit, ApplicationFailure>>
      Subscribe: (PackageExplorerEvent -> unit) -> System.IDisposable
      Close: unit -> Async<unit> }
