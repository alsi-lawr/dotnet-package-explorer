namespace Dotnet.PackageExplorer.Application

type SearchPackagesRequest =
    { Token: RequestToken
      Target: WorkspaceTarget
      Source: PackageSource option
      Query: SearchQuery }

type InstalledRefreshRequest =
    { Token: RequestToken
      Target: WorkspaceTarget }

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
    { Search: SearchPackagesRequest -> Async<Result<SearchPage, ApplicationFailure>>
      RefreshInstalled:
          InstalledRefreshRequest -> Async<Result<InstalledSnapshot, ApplicationFailure>>
      GetDetails: PackageDetailsRequest -> Async<Result<PackageDetails, ApplicationFailure>>
      GetReadme: PackageReadmeRequest -> Async<Result<PackageReadme, ApplicationFailure>>
      Preview: PreviewOperationRequest -> Async<Result<OperationPreview, ApplicationFailure>>
      Apply: ApplyOperationRequest -> Async<Result<OperationResult, ApplicationFailure>>
      Cancel: RequestToken -> Async<Result<unit, ApplicationFailure>>
      Close: unit -> Async<unit> }
