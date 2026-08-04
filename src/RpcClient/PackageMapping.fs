namespace Dotnet.PackageExplorer.RpcClient

open System
open Dotnet.PackageExplorer.Application

type internal PackageReference =
    { Version: PackageVersion option
      Source: PackageSource }

type internal InitializeNegotiation =
    { Capabilities: Set<string>
      MaximumFrameBytes: int
      MaximumPageSize: int
      MaximumDepth: int }

[<RequireQualifiedAccess>]
module internal PackageMapping =
    let private mapValues name mapping values =
        values
        |> List.mapi (fun index value -> mapping $"{name}[{index}]" value)
        |> List.fold
            (fun state item ->
                match state, item with
                | Ok values, Ok value -> Ok(value :: values)
                | Error failure, _
                | _, Error failure -> Error failure)
            (Ok [])
        |> Result.map List.rev

    let private mapField name mapping fields =
        RpcValue.field name fields |> Result.bind mapping

    let private textField name fields = RpcValue.requiredText name fields

    let private optionalText name fields = RpcValue.optionalText name fields

    let private arrayField name fields = RpcValue.requiredArray name fields

    let private nonEmptyText name value =
        RpcValue.text name value
        |> Result.bind (fun text ->
            if String.IsNullOrWhiteSpace text then
                Error $"{name} must be a non-empty string."
            else
                Ok text)

    let private nonEmptyTextField name fields =
        RpcValue.field name fields |> Result.bind (nonEmptyText name)

    let private mapObject name mapping value =
        RpcValue.fields name value |> Result.bind mapping

    let private textArray name fields =
        arrayField name fields
        |> Result.bind (mapValues name (fun itemName -> RpcValue.text itemName))

    let private requestId token =
        let (RequestToken value) = token
        let bytes = Array.zeroCreate<byte> 16
        Array.Copy(BitConverter.GetBytes value, bytes, sizeof<int64>)
        Guid bytes

    let requestIdentity token = requestId token

    let private validateRequestIdentity expected fields =
        result {
            let! requestId = nonEmptyTextField "requestId" fields

            match Guid.TryParse requestId with
            | true, actual when actual = expected -> return ()
            | _ -> return! Error "The package response request identity is invalid."
        }

    let private boundedInteger name minimum maximum value =
        RpcValue.number name value
        |> Result.bind (fun number ->
            if number < int64 minimum || number > int64 maximum then
                Error $"{name} is outside the supported range."
            else
                Ok(int number))

    let initialize () =
        RpcValue.map
            [ "protocolVersion",
              RpcValue.map [ "major", RpcValue.integer 1; "minor", RpcValue.integer 0 ]
              "clientInfo", RpcValue.map [ "name", RpcValue.string "dotnet-package-explorer" ]
              "capabilities", Protocol.capabilities |> Seq.map RpcValue.string |> RpcValue.array
              "limits",
              RpcValue.map
                  [ "maxFrameBytes", RpcValue.integer Protocol.NegotiatedFrameBytes
                    "maxPageSize", RpcValue.integer Protocol.MaximumPageSize ] ]

    let decodeInitialize value =
        mapObject
            "initialize result"
            (fun fields ->
                result {
                    let! version =
                        mapField "protocolVersion" (mapObject "protocolVersion" Ok) fields

                    let! major = mapField "major" (RpcValue.number "protocolVersion.major") version

                    let! minor = mapField "minor" (RpcValue.number "protocolVersion.minor") version

                    let! capabilities = textArray "capabilities" fields
                    let! server = mapField "serverInfo" (mapObject "serverInfo" Ok) fields
                    let! _ = nonEmptyTextField "name" server
                    let! _ = nonEmptyTextField "version" server
                    let! target = mapField "target" (mapObject "target" Ok) fields
                    let! _ = nonEmptyTextField "path" target
                    let! targetKind = nonEmptyTextField "kind" target
                    let! limits = mapField "limits" (mapObject "limits" Ok) fields

                    let! maximumFrameBytes =
                        mapField
                            "maxFrameBytes"
                            (boundedInteger
                                "limits.maxFrameBytes"
                                1024
                                Protocol.NegotiatedFrameBytes)
                            limits

                    let! maximumPageSize =
                        mapField
                            "maxPageSize"
                            (boundedInteger "limits.maxPageSize" 1 Protocol.MaximumPageSize)
                            limits

                    let! maximumDepth =
                        mapField
                            "maxDepth"
                            (boundedInteger "limits.maxDepth" 1 Protocol.MaximumDepth)
                            limits

                    if major <> 1L || minor < 0L then
                        let message =
                            $"Workspace Explorer package protocol {major}.{minor} "
                            + "is incompatible."

                        return! Error message

                    match targetKind with
                    | "solution"
                    | "solutionXml"
                    | "solutionFilter"
                    | "project:csharp"
                    | "project:fsharp"
                    | "project:visualBasic"
                    | "directory" -> ()
                    | _ -> return! Error "The package workspace target kind is invalid."

                    return
                        { Capabilities = Set.ofList capabilities
                          MaximumFrameBytes = maximumFrameBytes
                          MaximumPageSize = maximumPageSize
                          MaximumDepth = maximumDepth }
                })
            value

    let decodeAccepted expected value =
        mapObject
            "accepted result"
            (fun fields ->
                result {
                    let! accepted = mapField "accepted" (RpcValue.boolean "accepted") fields
                    do! validateRequestIdentity expected fields

                    if not accepted then
                        return! Error "The package request was not accepted."
                })
            value

    let decodeAcknowledgement value =
        mapObject
            "acknowledgement"
            (fun fields ->
                result {
                    let! accepted = mapField "accepted" (RpcValue.boolean "accepted") fields

                    if not accepted then
                        return! Error "The package request was not accepted."
                })
            value

    let searchParameters maximumPageSize (request: SearchPackagesRequest) continuation =
        let source =
            request.Source
            |> Option.map (fun (PackageSource source) -> source)
            |> Option.defaultValue ""

        RpcValue.map (
            [ "requestId", RpcValue.string ((requestId request.Token).ToString "D")
              "term", RpcValue.string request.Query.Text
              "includePrerelease", RpcValue.Boolean request.Query.IncludePrerelease
              "pageSize", RpcValue.integer (min maximumPageSize (max 1 request.Query.PageSize)) ]
            @ (if String.IsNullOrWhiteSpace source then
                   []
               else
                   [ "source", RpcValue.string source ])
            @ (continuation
               |> Option.map (fun value -> [ "continuation", RpcValue.string value ])
               |> Option.defaultValue [])
        )

    let sourcesParameters token =
        RpcValue.map [ "requestId", RpcValue.string ((requestId token).ToString "D") ]

    let decodeSources value =
        let source name value =
            mapObject
                name
                (fun fields ->
                    result {
                        let! identity = textField "id" fields
                        let! name = textField "name" fields
                        let! location = textField "location" fields
                        let! availability = textField "availability" fields

                        let! availability =
                            match availability with
                            | "available" -> Ok Available
                            | "disabled" -> Ok Disabled
                            | "authenticationRequired" -> Ok SourceAuthenticationRequired
                            | "unavailable" -> Ok Unavailable
                            | value -> Error $"Unknown package source availability '{value}'."

                        return
                            { Id = PackageSource identity
                              Name = name
                              Location = location
                              Availability = availability }
                    })
                value

        mapObject
            "sources result"
            (fun fields -> arrayField "sources" fields |> Result.bind (mapValues "sources" source))
            value

    let sourceMappingParameters (request: PackageSourceMappingRequest) =
        let (PackageId package) = request.Package

        RpcValue.map (
            [ "requestId", RpcValue.string ((requestId request.Token).ToString "D")
              "package", RpcValue.string package ]
            @ (request.Source
               |> Option.map (fun (PackageSource source) -> [ "source", RpcValue.string source ])
               |> Option.defaultValue [])
            @ (request.RestoredTransitives
               |> Option.map (fun packages ->
                   [ "restoredTransitives",
                     packages
                     |> Seq.map (fun (PackageId identity) -> RpcValue.string identity)
                     |> RpcValue.array ])
               |> Option.defaultValue [])
        )

    let decodeSourceMapping value =
        mapObject
            "source mapping"
            (fun fields ->
                result {
                    let! kind = textField "kind" fields
                    let! package = optionalText "package" fields
                    let! sources = textArray "sources" fields

                    let! kind =
                        match kind with
                        | "allowed" -> Ok ApplyAllowed
                        | "knownConflict" -> Ok KnownConflict
                        | "insufficientRestoredTransitiveEvidence" ->
                            Ok InsufficientTransitiveEvidence
                        | value -> Error $"Unknown package source mapping kind '{value}'."

                    return
                        { Kind = kind
                          Package = package |> Option.map PackageId
                          Sources = sources |> List.map PackageSource }
                })
            value

    let private summary name value =
        mapObject
            name
            (fun fields ->
                result {
                    let! package = textField "package" fields
                    let! version = textField "version" fields
                    let! source = textField "source" fields
                    let! description = optionalText "description" fields
                    let! summary = optionalText "summary" fields

                    return
                        { Id = PackageId package
                          DisplayName = package
                          InstalledVersion = None
                          LatestVersion = Some(PackageVersion version)
                          Kind = None
                          Source = Some(PackageSource source)
                          Relevance = None
                          Description = description |> Option.orElse summary },
                        { Version = Some(PackageVersion version)
                          Source = PackageSource source }
                })
            value

    let decodeSearch expectedRequestId (query: SearchQuery) value =
        mapObject
            "search page"
            (fun fields ->
                result {
                    do! validateRequestIdentity expectedRequestId fields
                    let! items = arrayField "items" fields
                    let! packages = mapValues "items" summary items
                    let! _ = arrayField "sourceFailures" fields
                    let! continuation = optionalText "continuation" fields

                    return
                        { Query = query
                          Packages = packages |> List.map fst
                          HasNextPage = Option.isSome continuation },
                        packages |> List.map (fun (package, reference) -> package.Id, reference),
                        continuation
                })
            value

    let private stateKind name fields =
        result {
            let! kind = textField "kind" fields

            match kind with
            | "direct" -> return Direct
            | "centrallyManagedDirect" -> return Central
            | "transitive" -> return Transitive
            | "frameworkProvided" -> return Framework
            | "unresolvedDirect" -> return Direct
            | "unresolvedCentrallyManagedDirect" -> return Central
            | _ -> return! Error $"Unknown {name} installed package kind '{kind}'."
        }

    let private targetIdentity name value =
        mapObject
            name
            (fun fields ->
                result {
                    let! project = nonEmptyTextField "project" fields
                    let! framework = optionalText "framework" fields
                    let! runtime = optionalText "runtime" fields

                    if runtime.IsSome && framework.IsNone then
                        return! Error $"{name}.runtime requires a target framework."

                    return
                        { Project = ProjectId project
                          Framework = framework |> Option.map TargetFramework
                          Runtime = runtime }
                })
            value

    let private installedPackage name value =
        mapObject
            name
            (fun fields ->
                result {
                    let! package = nonEmptyTextField "package" fields
                    let! target = mapField "target" (targetIdentity $"{name}.target") fields
                    let! state = mapField "state" (mapObject "state" Ok) fields
                    let! kind = stateKind "state.kind" state
                    let! resolved = optionalText "resolved" state

                    return
                        target,
                        { Id = PackageId package
                          DisplayName = package
                          InstalledVersion = resolved |> Option.map PackageVersion
                          LatestVersion = resolved |> Option.map PackageVersion
                          Kind = Some kind
                          Source = None
                          Relevance = None
                          Description = None }
                })
            value

    let private installedItem name value =
        mapObject
            name
            (fun fields ->
                result {
                    let! target = mapField "target" (targetIdentity $"{name}.target") fields
                    let! graphState = nonEmptyTextField "graphState" fields

                    match graphState with
                    | "current"
                    | "missing"
                    | "mismatched"
                    | "unverifiable"
                    | "stale" -> ()
                    | _ -> return! Error $"Unknown installed graph state '{graphState}'."

                    match Map.tryFind "package" fields with
                    | None
                    | Some RpcValue.Nil -> return None
                    | Some package ->
                        let! packageTarget, package = installedPackage $"{name}.package" package

                        if packageTarget <> target then
                            return! Error "The installed package target identity is inconsistent."

                        return Some { Target = target; Package = package }
                })
            value

    let decodeInstalled expectedRequestId value =
        mapObject
            "installed page"
            (fun fields ->
                result {
                    do! validateRequestIdentity expectedRequestId fields
                    let! items = arrayField "items" fields
                    let! packages = mapValues "items" installedItem items
                    let! restore = textField "restore" fields
                    let! continuation = optionalText "continuation" fields

                    match restore with
                    | "inProgress"
                    | "refreshed" -> ()
                    | _ -> return! Error $"Unknown installed restore state '{restore}'."

                    return
                        { Items = packages |> List.choose id
                          CapturedAt = DateTimeOffset.UtcNow },
                        restore,
                        continuation
                })
            value

    let installedParameters maximumPageSize (request: InstalledRefreshRequest) continuation =
        RpcValue.map (
            [ "requestId", RpcValue.string ((requestId request.Token).ToString "D")
              "pageSize", RpcValue.integer maximumPageSize ]
            @ (continuation
               |> Option.map (fun value -> [ "continuation", RpcValue.string value ])
               |> Option.defaultValue [])
        )

    let updatesParameters maximumPageSize (request: PackageUpdatesRequest) =
        RpcValue.map (
            [ "requestId", RpcValue.string ((requestId request.Token).ToString "D")
              "includePrerelease", RpcValue.Boolean request.IncludePrerelease
              "pageSize", RpcValue.integer (min maximumPageSize (max 1 request.PageSize)) ]
            @ (request.Continuation
               |> Option.map (fun value -> [ "continuation", RpcValue.string value ])
               |> Option.defaultValue [])
        )

    let decodeUpdates value =
        let update name value =
            mapObject
                name
                (fun fields ->
                    result {
                        let! package = textField "package" fields
                        let! target = mapField "target" (targetIdentity "target") fields
                        let! installed = optionalText "installedVersion" fields
                        let! available = textArray "available" fields

                        return
                            { Package = PackageId package
                              Target = target
                              InstalledVersion = installed |> Option.map PackageVersion
                              AvailableVersions = available |> List.map PackageVersion }
                    })
                value

        mapObject
            "updates page"
            (fun fields ->
                result {
                    let! values = arrayField "updates" fields
                    let! updates = mapValues "updates" update values
                    let! continuation = optionalText "continuation" fields

                    return
                        { Updates = updates
                          Continuation = continuation }
                })
            value

    let consolidationParameters maximumPageSize (request: PackageConsolidationRequest) =
        RpcValue.map (
            [ "requestId", RpcValue.string ((requestId request.Token).ToString "D")
              "pageSize", RpcValue.integer (min maximumPageSize (max 1 request.PageSize)) ]
            @ (request.Continuation
               |> Option.map (fun value -> [ "continuation", RpcValue.string value ])
               |> Option.defaultValue [])
        )

    let decodeConsolidation value =
        let currentVersion name value =
            mapObject
                name
                (fun fields ->
                    result {
                        let! version = textField "version" fields
                        let! targets = arrayField "targets" fields
                        let! targetValues = mapValues "targets" targetIdentity targets
                        return PackageVersion version, targetValues
                    })
                value

        let package name value =
            mapObject
                name
                (fun fields ->
                    result {
                        let! identity = textField "package" fields
                        let! current = arrayField "currentVersions" fields
                        let! currentVersions = mapValues "currentVersions" currentVersion current
                        let! candidates = textArray "candidateVersions" fields

                        return
                            { Package = PackageId identity
                              CurrentVersions = currentVersions
                              CandidateVersions = candidates |> List.map PackageVersion }
                    })
                value

        mapObject
            "consolidation page"
            (fun fields ->
                result {
                    let! values = arrayField "packages" fields
                    let! packages = mapValues "packages" package values
                    let! continuation = optionalText "continuation" fields

                    return
                        { Packages = packages
                          Continuation = continuation }
                })
            value

    let detailsParameters (request: PackageDetailsRequest) (reference: PackageReference) =
        let (PackageId package) = request.Package
        let (PackageSource source) = reference.Source

        let version =
            match reference.Version with
            | Some(PackageVersion version) ->
                RpcValue.map [ "kind", RpcValue.string "exact"; "value", RpcValue.string version ]
            | None -> RpcValue.map [ "kind", RpcValue.string "latest" ]

        RpcValue.map
            [ "requestId", RpcValue.string ((requestId request.Token).ToString "D")
              "package", RpcValue.string package
              "version", version
              "source", RpcValue.string source ]

    let private dependency name value =
        mapObject
            name
            (fun fields ->
                result {
                    let! package = textField "package" fields
                    let! version = textField "versionRange" fields

                    return
                        { Id = PackageId package
                          VersionRange = version }
                })
            value

    let private dependencies fields =
        result {
            let! groups = arrayField "dependencyGroups" fields

            let! dependencies =
                mapValues
                    "dependencyGroups"
                    (fun groupName group ->
                        mapObject
                            groupName
                            (fun groupFields ->
                                arrayField "dependencies" groupFields
                                |> Result.bind (mapValues "dependencies" dependency))
                            group)
                    groups

            return dependencies |> List.collect id |> List.distinct
        }

    let private vulnerability name value =
        mapObject
            name
            (fun fields ->
                result {
                    let! severity = textField "severity" fields
                    let! advisory = textField "advisory" fields

                    return
                        { Severity = severity
                          AdvisoryUrl = advisory }
                })
            value

    let decodeDetails value =
        mapObject
            "package details"
            (fun fields ->
                result {
                    let! summaryValue = RpcValue.field "summary" fields
                    let! package, _ = summary "summary" summaryValue
                    let! versions = textArray "versions" fields
                    let! dependencies = dependencies fields
                    let! deprecation = mapField "deprecation" (mapObject "deprecation" Ok) fields
                    let! deprecationKind = textField "kind" deprecation
                    let! vulnerabilitiesValue = arrayField "vulnerabilities" fields

                    let! vulnerabilities =
                        mapValues "vulnerabilities" vulnerability vulnerabilitiesValue

                    let! license = optionalText "license" fields
                    let! readme = optionalText "readmeCommonMark" fields

                    let! isDeprecated =
                        match deprecationKind with
                        | "notDeprecated" -> Ok false
                        | "deprecated" -> Ok true
                        | value -> Error $"Unknown package deprecation kind '{value}'."

                    return
                        { Package = package
                          Versions = versions |> List.map PackageVersion
                          Dependencies = dependencies
                          IsDeprecated = isDeprecated
                          Vulnerabilities = vulnerabilities
                          License = license },
                        readme
                })
            value

    let private projectTargets (target: WorkspaceTarget) (selection: TargetSelection) =
        let selectedProjects =
            if Set.isEmpty selection.Projects then
                WorkspaceTarget.projects target |> List.map _.Id |> Set.ofList
            else
                selection.Projects

        WorkspaceTarget.projects target
        |> List.filter (fun project -> selectedProjects.Contains project.Id)
        |> List.collect (fun project ->
            let frameworks =
                selection.Frameworks
                |> Map.tryFind project.Id
                |> Option.defaultValue (Set.ofList project.Frameworks)

            if Set.isEmpty frameworks then
                [ project.Id, None ]
            else
                frameworks
                |> Set.toList
                |> List.map (fun framework -> project.Id, Some framework))

    let private targetValue (ProjectId project, framework) =
        RpcValue.map (
            [ "project", RpcValue.string project ]
            @ (framework
               |> Option.map (fun (TargetFramework value) -> [ "framework", RpcValue.string value ])
               |> Option.defaultValue [])
        )

    let private operationValue operation =
        let selection =
            match operation with
            | InstallPackage(PackageId package, None) -> Ok(package, "installLatest", None)
            | InstallPackage(PackageId package, Some(PackageVersion version)) ->
                Ok(package, "installVersion", Some version)
            | UninstallPackage(PackageId package) -> Ok(package, "uninstall", None)
            | ConsolidatePackage(PackageId package, PackageVersion version) ->
                Ok(package, "consolidate", Some version)
            | UpdateSelectedPackages packages when packages.Count = 1 ->
                let (PackageId package) = Set.minElement packages
                Ok(package, "updateLatest", None)
            | UpdateSelectedPackages _ -> Error "Select at least one package to update."

        selection
        |> Result.map (fun (package, kind, version) ->
            RpcValue.map (
                [ "kind", RpcValue.string kind; "package", RpcValue.string package ]
                @ (version
                   |> Option.map (fun selected -> [ "version", RpcValue.string selected ])
                   |> Option.defaultValue [])
            ))

    let previewParameters (request: PreviewOperationRequest) =
        let targets = projectTargets request.Target request.Selection

        if List.isEmpty targets then
            Error "Select at least one package target."
        else
            match request.Operation with
            | UpdateSelectedPackages packages when packages.Count > 1 ->
                let updates =
                    [ for PackageId package in packages do
                          for target in targets do
                              yield
                                  RpcValue.map
                                      [ "package", RpcValue.string package
                                        "target", targetValue target ] ]

                Ok(
                    "package/previewBatch",
                    RpcValue.map
                        [ "requestId", RpcValue.string ((requestId request.Token).ToString "D")
                          "updates", RpcValue.array updates ],
                    true
                )
            | _ ->
                operationValue request.Operation
                |> Result.map (fun operation ->
                    "package/preview",
                    RpcValue.map
                        [ "requestId", RpcValue.string ((requestId request.Token).ToString "D")
                          "operation", operation
                          "targets", targets |> Seq.map targetValue |> RpcValue.array ],
                    false)

    let private currentVersion state =
        match state with
        | None
        | Some RpcValue.Nil -> Ok None
        | Some(RpcValue.Map fields) -> optionalText "resolved" fields
        | Some _ -> Error "The current package state is invalid."

    let private proposedVersion proposed =
        match proposed with
        | None
        | Some RpcValue.Nil -> Ok None
        | Some(RpcValue.Map fields) -> optionalText "version" fields
        | Some _ -> Error "The proposed package state is invalid."

    let private targetPreview name value =
        mapObject
            name
            (fun fields ->
                result {
                    let! target = mapField "target" (mapObject "target" Ok) fields
                    let! project = textField "project" target
                    let! framework = optionalText "framework" target
                    let! change = mapField "change" (mapObject "change" Ok) fields
                    let! ownerFiles = arrayField "ownerFiles" fields
                    let! _ = mapValues "ownerFiles" (fun name -> RpcValue.text name) ownerFiles
                    let! graphFreshness = textField "graphFreshness" fields
                    let! _ = mapField "impact" (mapObject "impact" Ok) fields
                    let! before = currentVersion (Map.tryFind "current" change)
                    let! after = proposedVersion (Map.tryFind "proposed" change)

                    match graphFreshness with
                    | "current"
                    | "awaitingRestore" -> ()
                    | _ -> return! Error $"Unknown graph freshness '{graphFreshness}'."

                    let! dependencies =
                        match Map.tryFind "impact" fields with
                        | Some(RpcValue.Map impact) ->
                            match Map.tryFind "metadata" impact with
                            | Some(RpcValue.Map metadata) ->
                                match Map.tryFind "dependencies" metadata with
                                | Some(RpcValue.Array values) ->
                                    mapValues "dependencies" dependency values
                                | _ -> Ok []
                            | _ -> Ok []
                        | _ -> Ok []

                    return
                        { Project = ProjectId project
                          Framework = framework |> Option.map TargetFramework
                          Before = before |> Option.map PackageVersion
                          After = after |> Option.map PackageVersion },
                        dependencies
                })
            value

    let private operationFromValue value =
        mapObject
            "operation"
            (fun fields ->
                result {
                    let! kind = textField "kind" fields
                    let! package = textField "package" fields
                    let! version = optionalText "version" fields
                    let packageId = PackageId package

                    match kind, version with
                    | "installLatest", None -> return InstallPackage(packageId, None)
                    | "installVersion", Some selected ->
                        return InstallPackage(packageId, Some(PackageVersion selected))
                    | "updateLatest", None ->
                        return UpdateSelectedPackages(Set.singleton packageId)
                    | "updateVersion", Some _ ->
                        return UpdateSelectedPackages(Set.singleton packageId)
                    | "uninstall", None -> return UninstallPackage packageId
                    | "consolidate", Some selected ->
                        return ConsolidatePackage(packageId, PackageVersion selected)
                    | _ -> return! Error $"Unknown package operation '{kind}'."
                })
            value

    let decodePreview (originalOperation: PackageOperation) batch value =
        let fingerprint name value =
            mapObject
                name
                (fun fields ->
                    result {
                        let! _ = nonEmptyTextField "path" fields
                        let! _ = nonEmptyTextField "fingerprint" fields
                        return ()
                    })
                value

        mapObject
            "preview"
            (fun fields ->
                result {
                    let! confirmation = textField "confirmationToken" fields
                    let! files = textArray "ownerFiles" fields
                    let! _ = nonEmptyTextField "workspaceRevision" fields
                    let! fingerprints = arrayField "fileFingerprints" fields
                    let! _ = mapValues "fileFingerprints" fingerprint fingerprints

                    let! operation =
                        if batch then
                            Ok originalOperation
                        else
                            mapField "operation" operationFromValue fields

                    let! previews =
                        if batch then
                            result {
                                let! updates = arrayField "updates" fields

                                return!
                                    mapValues
                                        "updates"
                                        (fun updateName update ->
                                            mapObject
                                                updateName
                                                (fun updateFields ->
                                                    result {
                                                        let! _ =
                                                            nonEmptyTextField
                                                                "package"
                                                                updateFields

                                                        return!
                                                            mapField
                                                                "targetPreview"
                                                                (targetPreview "targetPreview")
                                                                updateFields
                                                    })
                                                update)
                                        updates
                            }
                        else
                            arrayField "targets" fields
                            |> Result.bind (mapValues "targets" targetPreview)

                    return
                        { Id = PreviewId confirmation
                          Operation = operation
                          Summary =
                            [ $"{previews.Length} target change(s)"
                              $"{files.Length} owner file(s)" ]
                          Projects = previews |> List.map fst
                          Dependencies = previews |> List.collect snd |> List.distinct
                          Files = files }
                })
            value

    let executeParameters token confirmation =
        RpcValue.map
            [ "requestId", RpcValue.string ((requestId token).ToString "D")
              "confirmationToken", RpcValue.string confirmation ]

    let decodeExecution value =
        let executionEntry name value =
            mapObject
                name
                (fun fields ->
                    result {
                        let! _ = nonEmptyTextField "package" fields
                        let! _ = mapField "target" (targetIdentity $"{name}.target") fields
                        let! state = nonEmptyTextField "state" fields

                        match state with
                        | "completed"
                        | "compensated"
                        | "unchanged"
                        | "uncertain" -> return ()
                        | _ -> return! Error $"Unknown execution state '{state}'."
                    })
                value

        mapObject
            "execution result"
            (fun fields ->
                result {
                    let! operation = textField "operationId" fields
                    let! entries = arrayField "entries" fields
                    let! changed = textArray "changedFiles" fields
                    let! _ = mapValues "entries" executionEntry entries
                    let! restore = nonEmptyTextField "restore" fields

                    match Guid.TryParse operation with
                    | false, _ -> return! Error "The package operation identity is invalid."
                    | _ -> ()

                    match restore with
                    | "notRequired"
                    | "completed" -> ()
                    | _ -> return! Error $"Unknown execution restore state '{restore}'."

                    return
                        operation,
                        $"{entries.Length} target(s) completed; {changed.Length} file(s) changed."
                })
            value

    let decodeProgress token preview value =
        mapObject
            "operation progress"
            (fun fields ->
                result {
                    let! operation = textField "operationId" fields
                    let! stage = textField "stage" fields

                    let! completed =
                        match Map.tryFind "completed" fields with
                        | None -> Ok 0
                        | Some value -> boundedInteger "progress.completed" 0 Int32.MaxValue value

                    let! total =
                        match Map.tryFind "total" fields with
                        | None -> Ok 0
                        | Some value -> boundedInteger "progress.total" 0 Int32.MaxValue value

                    match Guid.TryParse operation with
                    | false, _ -> return! Error "The package operation identity is invalid."
                    | _ -> ()

                    match stage with
                    | "preparing"
                    | "applying"
                    | "restoring"
                    | "refreshing"
                    | "completed" -> ()
                    | _ -> return! Error $"Unknown package operation stage '{stage}'."

                    return
                        token,
                        { Preview = preview
                          Operation = OperationId operation
                          Completed = completed
                          Total = total
                          Status = stage }
                })
            value

    let private recoveryState =
        function
        | "completed" -> Ok Completed
        | "compensated" -> Ok Compensated
        | "unchanged" -> Ok Unchanged
        | "uncertain" -> Ok Uncertain
        | value -> Error $"Unknown recovery state '{value}'."

    let decodeRecovery value =
        match value with
        | Some(RpcValue.Map data) ->
            result {
                let! retry = nonEmptyTextField "retry" data

                match retry with
                | "never"
                | "afterUserAction"
                | "transient" -> ()
                | _ -> return! Error $"Unknown package recovery retry kind '{retry}'."

                let! values = arrayField "recovery" data

                return!
                    mapValues
                        "recovery"
                        (fun name entry ->
                            mapObject
                                name
                                (fun fields ->
                                    result {
                                        let! package = nonEmptyTextField "package" fields

                                        let! target =
                                            mapField "target" (targetIdentity "target") fields

                                        let! stateText = textField "state" fields
                                        let! state = recoveryState stateText

                                        return
                                            { Package = PackageId package
                                              Target = target
                                              State = state }
                                    })
                                entry)
                        values
            }
        | _ -> Error "The package recovery data is invalid."

    let cancelParameters token =
        RpcValue.map [ "requestId", RpcValue.string ((requestId token).ToString "D") ]

    let emptyParameters = RpcValue.map []
