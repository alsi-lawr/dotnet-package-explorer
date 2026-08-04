namespace Dotnet.PackageExplorer.RpcClient

open System
open System.Collections.Concurrent
open System.Threading
open Dotnet.PackageExplorer.Application

type RpcConnection =
    { Client: PackageExplorerClient
      Capabilities: Set<Capability>
      ServerCapabilities: Set<string> }

[<RequireQualifiedAccess>]
module RpcClient =
    let private applicationCapabilities capabilities =
        [ if capabilities |> Set.contains "packages.search.v1" then
              BrowsePackages
          if capabilities |> Set.contains "packages.installed.v1" then
              ReadInstalledPackages
          if
              capabilities |> Set.contains "packages.updates.v1"
              && capabilities |> Set.contains "packages.preview.v1"
              && capabilities |> Set.contains "packages.execute.v1"
          then
              UpdatePackages
          if
              capabilities |> Set.contains "packages.consolidation.v1"
              && capabilities |> Set.contains "packages.preview.v1"
              && capabilities |> Set.contains "packages.execute.v1"
          then
              ConsolidatePackages
          if capabilities |> Set.contains "packages.details.v1" then
              ReadPackageDetails
          if capabilities |> Set.contains "packages.readme.v1" then
              ReadPackageReadme
          if capabilities |> Set.contains "packages.preview.v1" then
              PreviewOperations
          if capabilities |> Set.contains "packages.execute.v1" then
              ApplyOperations ]
        |> Set.ofList

    let private scopeFailure scope kind message =
        { Scope = scope
          Kind = kind
          Message = message }

    let private transportFailure scope =
        function
        | TransportFailure.Unavailable ->
            scopeFailure
                scope
                BackendUnavailable
                "The dotnet host is unavailable. Install .NET 10 and try again."
        | TransportFailure.Exited code ->
            scopeFailure scope (BackendExited code) "Workspace Explorer exited unexpectedly."
        | TransportFailure.Cancelled ->
            scopeFailure scope Cancelled "The package request was cancelled."
        | TransportFailure.MalformedFrame ->
            scopeFailure
                scope
                (Rejected "invalid_response")
                "Workspace Explorer returned an invalid package response."
        | TransportFailure.OversizedFrame ->
            scopeFailure
                scope
                (Rejected "response_too_large")
                "Workspace Explorer returned a package response larger than the negotiated limit."
        | TransportFailure.TruncatedFrame ->
            scopeFailure
                scope
                (Rejected "truncated_response")
                "Workspace Explorer exited before its package response was complete."

    let private rpcFailure scope source (failure: RpcError) =
        let kind =
            match failure.Code with
            | "DWE-PACKAGE-AUTHENTICATION-REQUIRED"
            | "DWE-PACKAGE-SOURCE-AUTHENTICATION-REQUIRED" -> AuthenticationRequired source
            | "DWE-PACKAGE-CANCELLED" -> Cancelled
            | "DWE-PACKAGE-PARTIAL-RECOVERY" ->
                match PackageMapping.decodeRecovery failure.Data with
                | Ok recovery -> PartialRecoveryRequired recovery
                | Error _ -> Rejected "invalid_response"
            | "not_initialized" -> BackendIncompatible failure.Code
            | "unsupported_capability" -> BackendIncompatible failure.Code
            | code -> Rejected code

        scopeFailure scope kind failure.Message

    let private send
        (session: ProcessSession)
        scope
        source
        methodName
        parameters
        cancellationToken
        =
        task {
            let! outcome = session.SendAsync(methodName, parameters, cancellationToken)

            return
                match outcome with
                | Ok value -> Ok value
                | Error(Choice1Of2 failure) -> Error(transportFailure scope failure)
                | Error(Choice2Of2 failure) -> Error(rpcFailure scope source failure)
        }

    let private requestAsync operation =
        async {
            let! cancellationToken = Async.CancellationToken
            return! operation cancellationToken |> Async.AwaitTask
        }

    let internal startWith factory target =
        async {
            if String.IsNullOrWhiteSpace target then
                return
                    Error(
                        scopeFailure
                            BackendSessionFailure
                            (Rejected "invalid_target")
                            "Choose a solution, project, or directory."
                    )
            else
                match ProcessSession.Start(factory, target) with
                | Error failure -> return Error(transportFailure BackendSessionFailure failure)
                | Ok session ->
                    let! cancellationToken = Async.CancellationToken

                    let! initialized =
                        send
                            session
                            BackendSessionFailure
                            None
                            "initialize"
                            (PackageMapping.initialize ())
                            cancellationToken
                        |> Async.AwaitTask

                    match initialized with
                    | Error failure ->
                        do! session.CloseAsync() |> Async.AwaitTask
                        return Error failure
                    | Ok value ->
                        match PackageMapping.decodeInitialize value with
                        | Error message ->
                            do! session.CloseAsync() |> Async.AwaitTask

                            return
                                Error(
                                    scopeFailure
                                        BackendSessionFailure
                                        (BackendIncompatible message)
                                        message
                                )
                        | Ok negotiation ->
                            session.UseMaximumFrameBytes negotiation.MaximumFrameBytes
                            let serverCapabilities = negotiation.Capabilities
                            let references = ConcurrentDictionary<PackageId, PackageReference>()
                            let continuations = ConcurrentDictionary<string * int, string>()

                            let previews = ConcurrentDictionary<PreviewId, bool * string>()

                            let activeTokens = ConcurrentDictionary<Guid, RequestToken>()
                            let activeOperations = ConcurrentDictionary<Guid, PreviewId>()
                            let events = Event<PackageExplorerEvent>()

                            let interrupted
                                (scope: FailureScope)
                                (cancellationToken: CancellationToken)
                                (message: string)
                                =
                                match session.TerminalFailure with
                                | Some failure when not cancellationToken.IsCancellationRequested ->
                                    transportFailure scope failure
                                | _ -> scopeFailure scope Cancelled message

                            let publish (value: PackageExplorerEvent) =
                                try
                                    events.Trigger value
                                with _ ->
                                    ()

                            let invalidRestoreResponse =
                                "Workspace Explorer returned an invalid restore result."

                            let cancelledRestore = "Package restore was cancelled."

                            let notificationSubscription =
                                session.Notifications.Subscribe(fun (methodName, parameters) ->
                                    match parameters with
                                    | RpcValue.Map fields ->
                                        match RpcValue.optionalText "requestId" fields with
                                        | Ok(Some identityText) ->
                                            match Guid.TryParse identityText with
                                            | true, identity ->
                                                match activeTokens.TryGetValue identity with
                                                | true, token ->
                                                    match methodName with
                                                    | "package/restore/progress" ->
                                                        match
                                                            RpcValue.requiredText "state" fields
                                                        with
                                                        | Ok "inProgress" ->
                                                            publish (RestoreProgressed token)
                                                        | _ -> ()
                                                    | "package/installed/refreshed" ->
                                                        match
                                                            PackageMapping.decodeInstalled
                                                                identity
                                                                parameters
                                                        with
                                                        | Ok(snapshot, "refreshed", None) ->
                                                            publish (
                                                                InstalledRefreshed(token, snapshot)
                                                            )
                                                        | _ -> ()
                                                    | "package/restore/completed" ->
                                                        let outcome =
                                                            match
                                                                RpcValue.requiredText
                                                                    "state"
                                                                    fields,
                                                                Map.tryFind "error" fields
                                                            with
                                                            | Ok "refreshed", None -> Ok()
                                                            | Ok "cancelled", None ->
                                                                Error(
                                                                    scopeFailure
                                                                        BackendSessionFailure
                                                                        Cancelled
                                                                        cancelledRestore
                                                                )
                                                            | Ok "failed", Some(RpcValue.Map error)
                                                            | Ok "cancelled",
                                                              Some(RpcValue.Map error) ->
                                                                match
                                                                    RpcValue.requiredText
                                                                        "code"
                                                                        error,
                                                                    RpcValue.requiredText
                                                                        "message"
                                                                        error
                                                                with
                                                                | Ok code, Ok message ->
                                                                    Error(
                                                                        rpcFailure
                                                                            BackendSessionFailure
                                                                            None
                                                                            { Code = code
                                                                              Message = message
                                                                              Data =
                                                                                Map.tryFind
                                                                                    "data"
                                                                                    error }
                                                                    )
                                                                | _ ->
                                                                    Error(
                                                                        scopeFailure
                                                                            BackendSessionFailure
                                                                            (Rejected
                                                                                "invalid_response")
                                                                            invalidRestoreResponse
                                                                    )
                                                            | _ ->
                                                                Error(
                                                                    scopeFailure
                                                                        BackendSessionFailure
                                                                        (Rejected
                                                                            "invalid_response")
                                                                        invalidRestoreResponse
                                                                )

                                                        publish (RestoreCompleted(token, outcome))
                                                        activeTokens.TryRemove identity |> ignore
                                                    | "package/operations/progress" ->
                                                        match
                                                            Map.tryFind "progress" fields,
                                                            activeOperations.TryGetValue identity
                                                        with
                                                        | Some progress, (true, preview) ->
                                                            let decoded =
                                                                PackageMapping.decodeProgress
                                                                    token
                                                                    preview
                                                                    progress

                                                            match decoded with
                                                            | Ok(token, progress) ->
                                                                publish (
                                                                    OperationProgressed(
                                                                        token,
                                                                        progress
                                                                    )
                                                                )
                                                            | Error _ -> ()
                                                        | _ -> ()
                                                    | "package/operations/completed" ->
                                                        activeTokens.TryRemove identity |> ignore

                                                        activeOperations.TryRemove identity
                                                        |> ignore
                                                    | "package/search/completed"
                                                    | "package/updates/completed"
                                                    | "package/consolidation/completed" ->
                                                        activeTokens.TryRemove identity |> ignore
                                                    | _ -> ()
                                                | _ -> ()
                                            | _ -> ()
                                        | _ -> ()
                                    | _ -> ())

                            let search (request: SearchPackagesRequest) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        let source =
                                            request.Source
                                            |> Option.map (fun (PackageSource source) -> source)
                                            |> Option.defaultValue ""

                                        let signature =
                                            String.concat
                                                "\u001f"
                                                [ source
                                                  request.Query.Text
                                                  string request.Query.IncludePrerelease ]

                                        let key = signature, request.Query.Page

                                        let continuation =
                                            if request.Query.Page = 0 then
                                                Some None
                                            else
                                                match continuations.TryGetValue key with
                                                | true, value -> Some(Some value)
                                                | _ -> None

                                        match continuation with
                                        | None ->
                                            return
                                                Error(
                                                    scopeFailure
                                                        BackendSessionFailure
                                                        (Rejected "invalid_page")
                                                        ("The requested package page is no "
                                                         + "longer available.")
                                                )
                                        | Some continuation ->
                                            let identity =
                                                PackageMapping.requestIdentity request.Token

                                            activeTokens[identity] <- request.Token

                                            let wait =
                                                session.PrepareNotification(
                                                    "package/search/completed",
                                                    identity
                                                )

                                            let! accepted =
                                                send
                                                    session
                                                    BackendSessionFailure
                                                    request.Source
                                                    "package/search/start"
                                                    (PackageMapping.searchParameters
                                                        negotiation.MaximumPageSize
                                                        request
                                                        continuation)
                                                    cancellationToken

                                            let accepted =
                                                accepted
                                                |> Result.bind (fun value ->
                                                    PackageMapping.decodeAccepted identity value
                                                    |> Result.mapError (fun message ->
                                                        scopeFailure
                                                            BackendSessionFailure
                                                            (Rejected "invalid_response")
                                                            message))

                                            match accepted with
                                            | Error failure ->
                                                session.ForgetNotification(
                                                    "package/search/completed",
                                                    identity
                                                )

                                                activeTokens.TryRemove identity |> ignore
                                                return Error failure
                                            | Ok() ->
                                                try
                                                    let! completed =
                                                        wait.Task.WaitAsync cancellationToken

                                                    match completed with
                                                    | Error failure ->
                                                        return
                                                            Error(
                                                                rpcFailure
                                                                    BackendSessionFailure
                                                                    request.Source
                                                                    failure
                                                            )
                                                    | Ok page ->
                                                        match
                                                            PackageMapping.decodeSearch
                                                                identity
                                                                request.Query
                                                                page
                                                        with
                                                        | Error message ->
                                                            return
                                                                Error(
                                                                    scopeFailure
                                                                        BackendSessionFailure
                                                                        (Rejected
                                                                            "invalid_response")
                                                                        message
                                                                )
                                                        | Ok(page, packageReferences, next) ->
                                                            for package, reference in
                                                                packageReferences do
                                                                references[package] <- reference

                                                            match next with
                                                            | Some continuation ->
                                                                continuations[(fst key,
                                                                               request.Query.Page
                                                                               + 1)] <-
                                                                    continuation
                                                            | None -> ()

                                                            return Ok page
                                                with :? OperationCanceledException ->
                                                    return
                                                        Error(
                                                            interrupted
                                                                BackendSessionFailure
                                                                cancellationToken
                                                                "The package search was cancelled."
                                                        )
                                    })

                            let sources (request: PackageSourcesRequest) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        let! result =
                                            send
                                                session
                                                BackendSessionFailure
                                                None
                                                "package/sources"
                                                (PackageMapping.sourcesParameters request.Token)
                                                cancellationToken

                                        return
                                            result
                                            |> Result.bind (fun value ->
                                                PackageMapping.decodeSources value
                                                |> Result.mapError (fun message ->
                                                    scopeFailure
                                                        BackendSessionFailure
                                                        (Rejected "invalid_response")
                                                        message))
                                    })

                            let sourceMapping (request: PackageSourceMappingRequest) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        let! result =
                                            send
                                                session
                                                (PackageFailure request.Package)
                                                request.Source
                                                "package/sourceMapping"
                                                (PackageMapping.sourceMappingParameters request)
                                                cancellationToken

                                        return
                                            result
                                            |> Result.bind (fun value ->
                                                PackageMapping.decodeSourceMapping value
                                                |> Result.mapError (fun message ->
                                                    scopeFailure
                                                        (PackageFailure request.Package)
                                                        (Rejected "invalid_response")
                                                        message))
                                    })

                            let rec readInstalledPages
                                (request: InstalledRefreshRequest)
                                continuation
                                packages
                                cancellationToken
                                =
                                task {
                                    let identity = PackageMapping.requestIdentity request.Token

                                    let! result =
                                        let parameters =
                                            PackageMapping.installedParameters
                                                negotiation.MaximumPageSize
                                                request
                                                continuation

                                        send
                                            session
                                            BackendSessionFailure
                                            None
                                            "package/installed"
                                            parameters
                                            cancellationToken

                                    match result with
                                    | Error failure -> return Error failure
                                    | Ok value ->
                                        match PackageMapping.decodeInstalled identity value with
                                        | Error message ->
                                            return
                                                Error(
                                                    scopeFailure
                                                        BackendSessionFailure
                                                        (Rejected "invalid_response")
                                                        message
                                                )
                                        | Ok(snapshot, restore, next) ->
                                            let packages = packages @ snapshot.Items

                                            match next with
                                            | Some continuation ->
                                                return!
                                                    readInstalledPages
                                                        request
                                                        (Some continuation)
                                                        packages
                                                        cancellationToken
                                            | None ->
                                                return
                                                    Ok(
                                                        { Items = packages
                                                          CapturedAt = snapshot.CapturedAt },
                                                        restore
                                                    )
                                }

                            let refreshInstalled (request: InstalledRefreshRequest) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        let identity = PackageMapping.requestIdentity request.Token

                                        activeTokens[identity] <- request.Token

                                        let! result =
                                            readInstalledPages request None [] cancellationToken

                                        match result with
                                        | Error failure ->
                                            activeTokens.TryRemove identity |> ignore
                                            return Error failure
                                        | Ok(snapshot, restore) ->
                                            if restore = "refreshed" then
                                                activeTokens.TryRemove identity |> ignore

                                            return Ok snapshot
                                    })

                            let waitForDiscovery methodName notificationName scope request token =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        let identity = PackageMapping.requestIdentity token
                                        activeTokens[identity] <- token

                                        let wait =
                                            session.PrepareNotification(notificationName, identity)

                                        let! accepted =
                                            send
                                                session
                                                scope
                                                None
                                                methodName
                                                request
                                                cancellationToken

                                        let accepted =
                                            accepted
                                            |> Result.bind (fun value ->
                                                PackageMapping.decodeAccepted identity value
                                                |> Result.mapError (fun message ->
                                                    scopeFailure
                                                        scope
                                                        (Rejected "invalid_response")
                                                        message))

                                        match accepted with
                                        | Error failure ->
                                            session.ForgetNotification(notificationName, identity)
                                            activeTokens.TryRemove identity |> ignore
                                            return Error failure
                                        | Ok() ->
                                            try
                                                let! completed =
                                                    wait.Task.WaitAsync cancellationToken

                                                return
                                                    match completed with
                                                    | Ok result -> Ok result
                                                    | Error failure ->
                                                        Error(rpcFailure scope None failure)
                                            with :? OperationCanceledException ->
                                                return
                                                    Error(
                                                        interrupted
                                                            scope
                                                            cancellationToken
                                                            "The package request was cancelled."
                                                    )
                                    })

                            let findUpdates (request: PackageUpdatesRequest) =
                                async {
                                    let! result =
                                        waitForDiscovery
                                            "package/updates"
                                            "package/updates/completed"
                                            BackendSessionFailure
                                            (PackageMapping.updatesParameters
                                                negotiation.MaximumPageSize
                                                request)
                                            request.Token

                                    return
                                        result
                                        |> Result.bind (fun value ->
                                            PackageMapping.decodeUpdates value
                                            |> Result.mapError (fun message ->
                                                scopeFailure
                                                    BackendSessionFailure
                                                    (Rejected "invalid_response")
                                                    message))
                                }

                            let findConsolidation (request: PackageConsolidationRequest) =
                                async {
                                    let! result =
                                        waitForDiscovery
                                            "package/consolidation"
                                            "package/consolidation/completed"
                                            BackendSessionFailure
                                            (PackageMapping.consolidationParameters
                                                negotiation.MaximumPageSize
                                                request)
                                            request.Token

                                    return
                                        result
                                        |> Result.bind (fun value ->
                                            PackageMapping.decodeConsolidation value
                                            |> Result.mapError (fun message ->
                                                scopeFailure
                                                    BackendSessionFailure
                                                    (Rejected "invalid_response")
                                                    message))
                                }

                            let resolveReference token target package cancellationToken =
                                task {
                                    match references.TryGetValue package with
                                    | true, reference -> return Ok reference
                                    | _ ->
                                        let mappingRequest: PackageSourceMappingRequest =
                                            { Token = token
                                              Target = target
                                              Package = package
                                              Source = None
                                              RestoredTransitives = None }

                                        let! mapping =
                                            send
                                                session
                                                (PackageFailure package)
                                                None
                                                "package/sourceMapping"
                                                (PackageMapping.sourceMappingParameters
                                                    mappingRequest)
                                                cancellationToken

                                        match mapping with
                                        | Error failure -> return Error failure
                                        | Ok value ->
                                            match PackageMapping.decodeSourceMapping value with
                                            | Ok mapping when not (List.isEmpty mapping.Sources) ->
                                                let reference =
                                                    { Version = None
                                                      Source = mapping.Sources.Head }

                                                references[package] <- reference
                                                return Ok reference
                                            | Ok _ ->
                                                return
                                                    Error(
                                                        scopeFailure
                                                            (PackageFailure package)
                                                            (Rejected "source_unavailable")
                                                            ("No configured source can provide "
                                                             + "this package.")
                                                    )
                                            | Error message ->
                                                return
                                                    Error(
                                                        scopeFailure
                                                            (PackageFailure package)
                                                            (Rejected "invalid_response")
                                                            message
                                                    )
                                }

                            let getDetails (request: PackageDetailsRequest) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        let! resolved =
                                            resolveReference
                                                request.Token
                                                request.Target
                                                request.Package
                                                cancellationToken

                                        match resolved with
                                        | Error failure -> return Error failure
                                        | Ok reference ->
                                            let! result =
                                                send
                                                    session
                                                    (PackageFailure request.Package)
                                                    (Some reference.Source)
                                                    "package/details"
                                                    (PackageMapping.detailsParameters
                                                        request
                                                        reference)
                                                    cancellationToken

                                            return
                                                result
                                                |> Result.bind (fun value ->
                                                    PackageMapping.decodeDetails value
                                                    |> Result.map fst
                                                    |> Result.mapError (fun message ->
                                                        scopeFailure
                                                            (PackageFailure request.Package)
                                                            (Rejected "invalid_response")
                                                            message))
                                    })

                            let getReadme (request: PackageReadmeRequest) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        let! resolved =
                                            resolveReference
                                                request.Token
                                                request.Target
                                                request.Package
                                                cancellationToken

                                        match resolved with
                                        | Error failure -> return Error failure
                                        | Ok reference ->
                                            let detailsRequest: PackageDetailsRequest =
                                                { Token = request.Token
                                                  Target = request.Target
                                                  Package = request.Package }

                                            let! result =
                                                send
                                                    session
                                                    (PackageFailure request.Package)
                                                    (Some reference.Source)
                                                    "package/details"
                                                    (PackageMapping.detailsParameters
                                                        detailsRequest
                                                        reference)
                                                    cancellationToken

                                            return
                                                result
                                                |> Result.bind (fun value ->
                                                    PackageMapping.decodeDetails value
                                                    |> Result.bind (fun (_, readme) ->
                                                        match readme with
                                                        | Some commonMark ->
                                                            Ok
                                                                { Package = request.Package
                                                                  CommonMark = commonMark }
                                                        | None ->
                                                            Error(
                                                                "This package does not provide "
                                                                + "a README."
                                                            ))
                                                    |> Result.mapError (fun message ->
                                                        scopeFailure
                                                            (PackageFailure request.Package)
                                                            (Rejected "readme_unavailable")
                                                            message))
                                    })

                            let preview (request: PreviewOperationRequest) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        match PackageMapping.previewParameters request with
                                        | Error message ->
                                            return
                                                Error(
                                                    scopeFailure
                                                        (OperationFailure None)
                                                        (Rejected "invalid_target")
                                                        message
                                                )
                                        | Ok(methodName, parameters, batch) ->
                                            let! result =
                                                send
                                                    session
                                                    (OperationFailure None)
                                                    None
                                                    methodName
                                                    parameters
                                                    cancellationToken

                                            match result with
                                            | Error failure -> return Error failure
                                            | Ok value ->
                                                match
                                                    PackageMapping.decodePreview
                                                        request.Operation
                                                        batch
                                                        value
                                                with
                                                | Error message ->
                                                    return
                                                        Error(
                                                            scopeFailure
                                                                (OperationFailure None)
                                                                (Rejected "invalid_response")
                                                                message
                                                        )
                                                | Ok preview ->
                                                    let (PreviewId confirmation) = preview.Id
                                                    previews[preview.Id] <- batch, confirmation
                                                    return Ok preview
                                    })

                            let apply (request: ApplyOperationRequest) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        match previews.TryRemove request.Preview with
                                        | false, _ ->
                                            return
                                                Error(
                                                    scopeFailure
                                                        (OperationFailure(Some request.Preview))
                                                        (Rejected "unknown_preview")
                                                        ("Preview the package change again "
                                                         + "before applying it.")
                                                )
                                        | true, (batch, confirmation) ->
                                            let identity =
                                                PackageMapping.requestIdentity request.Token

                                            activeTokens[identity] <- request.Token
                                            activeOperations[identity] <- request.Preview

                                            let methodName =
                                                if batch then
                                                    "package/executeBatch/start"
                                                else
                                                    "package/execute/start"

                                            let wait =
                                                session.PrepareNotification(
                                                    "package/operations/completed",
                                                    identity
                                                )

                                            let! accepted =
                                                send
                                                    session
                                                    (OperationFailure(Some request.Preview))
                                                    None
                                                    methodName
                                                    (PackageMapping.executeParameters
                                                        request.Token
                                                        confirmation)
                                                    cancellationToken

                                            let accepted =
                                                accepted
                                                |> Result.bind (fun value ->
                                                    PackageMapping.decodeAccepted identity value
                                                    |> Result.mapError (fun message ->
                                                        scopeFailure
                                                            (OperationFailure(
                                                                Some request.Preview
                                                            ))
                                                            (Rejected "invalid_response")
                                                            message))

                                            match accepted with
                                            | Error failure ->
                                                session.ForgetNotification(
                                                    "package/operations/completed",
                                                    identity
                                                )

                                                activeTokens.TryRemove identity |> ignore
                                                activeOperations.TryRemove identity |> ignore
                                                return Error failure
                                            | Ok() ->
                                                try
                                                    let! completed =
                                                        wait.Task.WaitAsync cancellationToken

                                                    match completed with
                                                    | Error failure ->
                                                        return
                                                            Error(
                                                                rpcFailure
                                                                    (OperationFailure(
                                                                        Some request.Preview
                                                                    ))
                                                                    None
                                                                    failure
                                                            )
                                                    | Ok result ->
                                                        match
                                                            PackageMapping.decodeExecution result
                                                        with
                                                        | Error message ->
                                                            return
                                                                Error(
                                                                    scopeFailure
                                                                        (OperationFailure(
                                                                            Some request.Preview
                                                                        ))
                                                                        (Rejected
                                                                            "invalid_response")
                                                                        message
                                                                )
                                                        | Ok(_, summary) ->
                                                            let refreshToken =
                                                                RequestToken(
                                                                    DateTime.UtcNow.Ticks
                                                                    &&& Int64.MaxValue
                                                                )

                                                            let refreshRequest
                                                                : InstalledRefreshRequest =
                                                                { Token = refreshToken
                                                                  Target = request.Target }

                                                            let! installed =
                                                                readInstalledPages
                                                                    refreshRequest
                                                                    None
                                                                    []
                                                                    cancellationToken

                                                            match installed with
                                                            | Error failure -> return Error failure
                                                            | Ok(snapshot, _) ->
                                                                return
                                                                    Ok
                                                                        { Preview = request.Preview
                                                                          Installed = snapshot
                                                                          Summary = summary }
                                                with :? OperationCanceledException ->
                                                    return
                                                        Error(
                                                            interrupted
                                                                (OperationFailure(
                                                                    Some request.Preview
                                                                ))
                                                                cancellationToken
                                                                ("The package operation was "
                                                                 + "cancelled.")
                                                        )
                                    })

                            let cancel (token: RequestToken) =
                                requestAsync (fun cancellationToken ->
                                    task {
                                        let identity = PackageMapping.requestIdentity token
                                        activeTokens.TryRemove identity |> ignore
                                        activeOperations.TryRemove identity |> ignore

                                        [ "package/search/completed"
                                          "package/updates/completed"
                                          "package/consolidation/completed"
                                          "package/operations/completed" ]
                                        |> List.iter (fun methodName ->
                                            session.ForgetNotification(methodName, identity))

                                        let! result =
                                            send
                                                session
                                                BackendSessionFailure
                                                None
                                                "package/cancel"
                                                (PackageMapping.cancelParameters token)
                                                cancellationToken

                                        return
                                            result
                                            |> Result.bind (fun value ->
                                                PackageMapping.decodeAcknowledgement value
                                                |> Result.mapError (fun message ->
                                                    scopeFailure
                                                        BackendSessionFailure
                                                        (Rejected "invalid_response")
                                                        message))
                                    })

                            let mutable closed = 0

                            let close () =
                                async {
                                    if Interlocked.Exchange(&closed, 1) = 0 then
                                        notificationSubscription.Dispose()
                                        do! session.CloseAsync() |> Async.AwaitTask
                                }

                            let client =
                                { Sources = sources
                                  SourceMapping = sourceMapping
                                  Search = search
                                  RefreshInstalled = refreshInstalled
                                  FindUpdates = findUpdates
                                  FindConsolidation = findConsolidation
                                  GetDetails = getDetails
                                  GetReadme = getReadme
                                  Preview = preview
                                  Apply = apply
                                  Cancel = cancel
                                  Subscribe = fun observer -> events.Publish.Subscribe observer
                                  Close = close }

                            return
                                Ok
                                    { Client = client
                                      Capabilities = applicationCapabilities serverCapabilities
                                      ServerCapabilities = serverCapabilities }
        }

    let connect target = startWith ProcessFactory.system target
