namespace Dotnet.PackageExplorer.Application

type PackageId = PackageId of string

type PackageVersion = PackageVersion of string

type PackageSource = PackageSource of string

type PackageSourceAvailability =
    | Available
    | Disabled
    | SourceAuthenticationRequired
    | Unavailable

type PackageSourceInfo =
    { Id: PackageSource
      Name: string
      Location: string
      Availability: PackageSourceAvailability }

type ProjectId = ProjectId of string

type TargetFramework = TargetFramework of string

type PackageTargetIdentity =
    { Project: ProjectId
      Framework: TargetFramework option
      Runtime: string option }

type RequestToken = RequestToken of int64

type PreviewId = PreviewId of string

type PackageKind =
    | Direct
    | Transitive
    | Central
    | Framework

type PackageSummary =
    { Id: PackageId
      DisplayName: string
      InstalledVersion: PackageVersion option
      LatestVersion: PackageVersion option
      Kind: PackageKind option
      Source: PackageSource option
      Relevance: float option
      Description: string option }

type PackageDependency = { Id: PackageId; VersionRange: string }

type PackageVulnerability =
    { Severity: string
      AdvisoryUrl: string }

type PackageDetails =
    { Package: PackageSummary
      Versions: PackageVersion list
      Dependencies: PackageDependency list
      IsDeprecated: bool
      Vulnerabilities: PackageVulnerability list
      License: string option }

type PackageReadme =
    { Package: PackageId
      CommonMark: string }

type ProjectTarget =
    { Id: ProjectId
      Name: string
      Frameworks: TargetFramework list }

type WorkspaceTarget =
    | SingleProject of ProjectTarget
    | Solution of path: string * projects: ProjectTarget list
    | Workspace of path: string * projects: ProjectTarget list

module WorkspaceTarget =
    let projects target =
        match target with
        | SingleProject project -> [ project ]
        | Solution(_, projects)
        | Workspace(_, projects) -> projects

    let supportsProjectSelection target =
        match target with
        | SingleProject _ -> false
        | Solution _
        | Workspace _ -> true

type PackageMode =
    | Browse
    | Installed
    | Updates
    | Consolidate

type Capability =
    | BrowsePackages
    | ReadInstalledPackages
    | UpdatePackages
    | ConsolidatePackages
    | ReadPackageDetails
    | ReadPackageReadme
    | PreviewOperations
    | ApplyOperations

module Capability =
    let requiredForMode mode =
        match mode with
        | Browse -> BrowsePackages
        | Installed -> ReadInstalledPackages
        | Updates -> UpdatePackages
        | Consolidate -> ConsolidatePackages

type SortField =
    | Relevance
    | Name
    | Version
    | Type

type SortDirection =
    | Ascending
    | Descending

type PackageSort =
    { Field: SortField
      Direction: SortDirection }

module PackageSort =
    let defaultForMode mode =
        match mode with
        | Browse ->
            { Field = Relevance
              Direction = Descending }
        | Installed
        | Updates
        | Consolidate -> { Field = Name; Direction = Ascending }

    let private compareOption direction comparer left right =
        match left, right with
        | Some leftValue, Some rightValue ->
            let compared = comparer leftValue rightValue

            match direction with
            | Ascending -> compared
            | Descending -> -compared
        | Some _, None -> -1
        | None, Some _ -> 1
        | None, None -> 0

    let private compareField sort left right =
        match sort.Field with
        | Relevance -> compareOption sort.Direction compare left.Relevance right.Relevance
        | Name ->
            let compared =
                System.StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName)

            match sort.Direction with
            | Ascending -> compared
            | Descending -> -compared
        | Version -> compareOption sort.Direction compare left.LatestVersion right.LatestVersion
        | Type -> compareOption sort.Direction compare left.Kind right.Kind

    let apply mode sort packages =
        let installedStateMode = mode <> Browse

        let directRank package =
            if installedStateMode && package.Kind = Some Direct then
                0
            else
                1

        packages
        |> List.mapi (fun index package -> index, package)
        |> List.sortWith (fun (leftIndex, left) (rightIndex, right) ->
            let kindOrder = compare (directRank left) (directRank right)

            if kindOrder <> 0 then
                kindOrder
            else
                let directedOrder = compareField sort left right

                if directedOrder <> 0 then
                    directedOrder
                else
                    compare leftIndex rightIndex)
        |> List.map snd

type SearchQuery =
    { Text: string
      IncludePrerelease: bool
      Page: int
      PageSize: int }

type SearchPage =
    { Query: SearchQuery
      Packages: PackageSummary list
      HasNextPage: bool }

type InstalledPackage =
    { Target: PackageTargetIdentity
      Package: PackageSummary }

type InstalledSnapshot =
    { Items: InstalledPackage list
      CapturedAt: System.DateTimeOffset }

[<RequireQualifiedAccess>]
module InstalledSnapshot =
    let packages snapshot =
        snapshot.Items |> List.map _.Package |> List.distinctBy _.Id

type PackageSourceMappingKind =
    | ApplyAllowed
    | KnownConflict
    | InsufficientTransitiveEvidence

type PackageSourceMapping =
    { Kind: PackageSourceMappingKind
      Package: PackageId option
      Sources: PackageSource list }

type PackageUpdate =
    { Package: PackageId
      Target: PackageTargetIdentity
      InstalledVersion: PackageVersion option
      AvailableVersions: PackageVersion list }

type PackageUpdatesPage =
    { Updates: PackageUpdate list
      Continuation: string option }

type PackageConsolidation =
    { Package: PackageId
      CurrentVersions: (PackageVersion * PackageTargetIdentity list) list
      CandidateVersions: PackageVersion list }

type PackageConsolidationPage =
    { Packages: PackageConsolidation list
      Continuation: string option }

type TargetSelection =
    { Projects: Set<ProjectId>
      Frameworks: Map<ProjectId, Set<TargetFramework>> }

module TargetSelection =
    let forTarget target =
        match target with
        | SingleProject project ->
            { Projects = Set.singleton project.Id
              Frameworks = Map.ofList [ project.Id, Set.ofList project.Frameworks ] }
        | Solution _
        | Workspace _ ->
            { Projects = Set.empty
              Frameworks = Map.empty }

type PackageOperation =
    | InstallPackage of PackageId * PackageVersion option
    | UpdateSelectedPackages of Set<PackageId>
    | UninstallPackage of PackageId
    | ConsolidatePackage of PackageId * PackageVersion

type PreviewProject =
    { Project: ProjectId
      Framework: TargetFramework option
      Before: PackageVersion option
      After: PackageVersion option }

type OperationPreview =
    { Id: PreviewId
      Operation: PackageOperation
      Summary: string list
      Projects: PreviewProject list
      Dependencies: PackageDependency list
      Files: string list }

type PreviewTab =
    | Summary
    | Projects
    | Dependencies
    | Files

type OperationId = OperationId of string

type OperationProgress =
    { Preview: PreviewId
      Operation: OperationId
      Completed: int
      Total: int
      Status: string }

type OperationResult =
    { Preview: PreviewId
      Installed: InstalledSnapshot
      Summary: string }

type RecoveryState =
    | Completed
    | Compensated
    | Unchanged
    | Uncertain

type RecoveryEntry =
    { Package: PackageId
      Target: PackageTargetIdentity
      State: RecoveryState }

type FailureKind =
    | AuthenticationRequired of PackageSource option
    | BackendUnavailable
    | BackendIncompatible of string
    | BackendExited of exitCode: int option
    | Cancelled
    | PartialRecoveryRequired of RecoveryEntry list
    | Rejected of string

type FailureScope =
    | SourceFailure of PackageSource
    | PackageFailure of PackageId
    | ProjectFailure of ProjectId
    | OperationFailure of PreviewId option
    | BackendSessionFailure

type ApplicationFailure =
    { Scope: FailureScope
      Kind: FailureKind
      Message: string }

type PackageExplorerEvent =
    | InstalledRefreshed of RequestToken * InstalledSnapshot
    | RestoreProgressed of RequestToken
    | RestoreCompleted of RequestToken * Result<unit, ApplicationFailure>
    | OperationProgressed of RequestToken * OperationProgress

type RequestKind =
    | SearchRequest
    | RefreshRequest
    | DetailsRequest
    | ReadmeRequest
    | PreviewRequest
    | ApplyRequest
