namespace Dotnet.PackageExplorer.RpcClient.UnitTests

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Threading
open System.Threading.Tasks
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open FsUnit.Xunit
open Xunit

type private ScriptedProcess(?errorText: string) =
    let inputServer =
        new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)

    let backendInput =
        new AnonymousPipeClientStream(PipeDirection.In, inputServer.GetClientHandleAsString())

    let outputServer =
        new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None)

    let backendOutput =
        new AnonymousPipeClientStream(PipeDirection.Out, outputServer.GetClientHandleAsString())

    let exited =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable killCount = 0
    let errorText = defaultArg errorText ""

    member _.BackendInput = backendInput
    member _.BackendOutput = backendOutput
    member _.KillCount = killCount

    member _.CloseOutput() = backendOutput.Dispose()
    member _.Exit(exitCode) = exited.TrySetResult exitCode |> ignore

    member _.Complete(exitCode) =
        backendOutput.Dispose()
        exited.TrySetResult exitCode |> ignore

    interface IProcessHandle with
        member _.Input = inputServer
        member _.Output = outputServer
        member _.Error = new StringReader(errorText)
        member _.HasExited = exited.Task.IsCompleted

        member _.ExitCode =
            if exited.Task.IsCompletedSuccessfully then
                Some exited.Task.Result
            else
                None

        member _.WaitForExitAsync cancellationToken =
            task {
                let! _ = exited.Task.WaitAsync cancellationToken
                return ()
            }

        member _.KillTree() =
            Interlocked.Increment(&killCount) |> ignore
            exited.TrySetResult 137 |> ignore

        member _.DisposeAsync() =
            inputServer.Dispose()
            outputServer.Dispose()
            backendInput.Dispose()
            backendOutput.Dispose()
            ValueTask.CompletedTask

[<RequireQualifiedAccess>]
module private Script =
    let private map = RpcValue.map
    let private text = RpcValue.string

    let readFrame (stream: Stream) =
        task {
            let bytes = ResizeArray<byte>()
            let buffer = Array.zeroCreate<byte> 256
            let mutable frame = None

            while frame.IsNone do
                let! read = stream.ReadAsync(buffer.AsMemory()).AsTask()

                if read = 0 then
                    failwith "The scripted client stream ended before a complete frame."

                for index in 0 .. read - 1 do
                    bytes.Add buffer[index]

                match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte>(bytes.ToArray())) with
                | Ok(value, consumed) when consumed = bytes.Count -> frame <- Some value
                | Ok _ -> failwith "The scripted client wrote trailing protocol data."
                | Error DecodeFailure.Incomplete -> ()
                | Error failure -> failwith $"The scripted client wrote {failure}."

            return frame.Value
        }

    let writeFrame (stream: Stream) frame =
        task {
            let bytes = MessagePackCodec.encode frame
            do! stream.WriteAsync(bytes.AsMemory()).AsTask()
            do! stream.FlushAsync()
        }

    let initializeResult (major: int) (maximumFrameBytes: int) (capabilities: string seq) =
        map
            [ "protocolVersion",
              map [ "major", RpcValue.integer major; "minor", RpcValue.integer 0 ]
              "serverInfo", map [ "name", text "dotnet-workspace-explorer"; "version", text "1" ]
              "target", map [ "path", text "/workspace/App.fsproj"; "kind", text "project:fsharp" ]
              "capabilities", capabilities |> Seq.map text |> RpcValue.array
              "limits",
              map
                  [ "maxFrameBytes", RpcValue.integer maximumFrameBytes
                    "maxPageSize", RpcValue.integer 25
                    "maxDepth", RpcValue.integer 64 ] ]

    let initialize (scripted: ScriptedProcess) (maximumFrameBytes: int) (capabilities: string seq) =
        task {
            let! request = readFrame scripted.BackendInput

            match request with
            | RpcFrame.Request(messageId, "initialize", RpcValue.Map _) ->
                do!
                    writeFrame
                        scripted.BackendOutput
                        (RpcFrame.Response(
                            messageId,
                            Ok(initializeResult 1 maximumFrameBytes capabilities)
                        ))
            | actual -> failwith $"Expected initialize first, got {actual}."
        }

    let request (methodName: string) (scripted: ScriptedProcess) =
        task {
            let! frame = readFrame scripted.BackendInput

            match frame with
            | RpcFrame.Request(messageId, actualMethod, RpcValue.Map fields) when
                actualMethod = methodName
                ->
                return messageId, fields
            | actual -> return failwith $"Expected {methodName}, got {actual}."
        }

    let requestIdentity (fields: Map<string, RpcValue>) =
        fields
        |> RpcValue.requiredText "requestId"
        |> Result.defaultWith failwith
        |> Guid.Parse

    let accepted (scripted: ScriptedProcess) (messageId: uint32) (requestId: Guid) =
        writeFrame
            scripted.BackendOutput
            (RpcFrame.Response(
                messageId,
                Ok(
                    map
                        [ "accepted", RpcValue.Boolean true
                          "requestId", text (requestId.ToString "D") ]
                )
            ))

    let acknowledged (scripted: ScriptedProcess) (messageId: uint32) =
        writeFrame
            scripted.BackendOutput
            (RpcFrame.Response(messageId, Ok(map [ "accepted", RpcValue.Boolean true ])))

    let notify (scripted: ScriptedProcess) (methodName: string) (fields: (string * RpcValue) list) =
        writeFrame scripted.BackendOutput (RpcFrame.Notification(methodName, map fields))

    let target (project: string) (framework: string option) =
        map (
            [ "project", text project ]
            @ (framework
               |> Option.map (fun value -> [ "framework", text value ])
               |> Option.defaultValue [])
        )

    let installedItem (project: string) (framework: string option) (package: string) =
        let identity = target project framework

        map
            [ "target", identity
              "graphState", text "current"
              "package",
              map
                  [ "package", text package
                    "target", identity
                    "state", map [ "kind", text "direct"; "resolved", text "1.0.0" ] ] ]

    let installedPage
        (requestId: Guid)
        (restore: string)
        (items: RpcValue seq)
        (continuation: string option)
        =
        map (
            [ "requestId", text (requestId.ToString "D")
              "restore", text restore
              "items", RpcValue.array items ]
            @ (continuation
               |> Option.map (fun value -> [ "continuation", text value ])
               |> Option.defaultValue [])
        )

    let shutdown (scripted: ScriptedProcess) complete =
        task {
            let! messageId, _ = request "shutdown" scripted
            do! acknowledged scripted messageId

            if complete then
                scripted.Complete 0
        }

[<Sealed>]
type ProcessSessionTests() =
    [<Fact>]
    member _.``connection owns one exact dotnet package pipe process and negotiates before use``() =
        let scripted = ScriptedProcess()
        let mutable launch: ProcessStartInfo option = None

        let factory startInfo =
            launch <- Some startInfo
            Ok(scripted :> IProcessHandle)

        let backend =
            task {
                let! initialize = Script.readFrame scripted.BackendInput

                match initialize with
                | RpcFrame.Request(messageId, "initialize", RpcValue.Map _) ->
                    let result =
                        RpcValue.map
                            [ "protocolVersion",
                              RpcValue.map
                                  [ "major", RpcValue.integer 1; "minor", RpcValue.integer 0 ]
                              "serverInfo",
                              RpcValue.map
                                  [ "name", RpcValue.string "dotnet-workspace-explorer"
                                    "version", RpcValue.string "1" ]
                              "target",
                              RpcValue.map
                                  [ "path", RpcValue.string "/workspace/App.fsproj"
                                    "kind", RpcValue.string "project:fsharp" ]
                              "capabilities",
                              RpcValue.array
                                  [ RpcValue.string "packages.search.v1"
                                    RpcValue.string "packages.details.v1"
                                    RpcValue.string "future.capability.v9" ]
                              "limits",
                              RpcValue.map
                                  [ "maxFrameBytes", RpcValue.integer 65536
                                    "maxPageSize", RpcValue.integer 25
                                    "maxDepth", RpcValue.integer 64 ] ]

                    do!
                        Script.writeFrame
                            scripted.BackendOutput
                            (RpcFrame.Response(messageId, Ok result))
                | actual -> failwith $"Expected initialize first, got {actual}."

                let! shutdown = Script.readFrame scripted.BackendInput

                match shutdown with
                | RpcFrame.Request(messageId, "shutdown", RpcValue.Map fields) when
                    Map.isEmpty fields
                    ->
                    do!
                        Script.writeFrame
                            scripted.BackendOutput
                            (RpcFrame.Response(
                                messageId,
                                Ok(RpcValue.map [ "accepted", RpcValue.Boolean true ])
                            ))
                | actual -> failwith $"Expected an orderly shutdown, got {actual}."

                scripted.Complete 0
            }

        let connection =
            RpcClient.startWith factory "/workspace/App.fsproj"
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        let startInfo =
            launch |> Option.defaultWith (fun () -> failwith "No process was launched.")

        startInfo.FileName |> should equal "dotnet"
        startInfo.UseShellExecute |> should equal false

        startInfo.ArgumentList
        |> Seq.toList
        |> should equal [ "we"; "packages"; "/workspace/App.fsproj"; "--pipe" ]

        connection.Capabilities
        |> should equal (set [ BrowsePackages; ReadPackageDetails ])

        connection.ServerCapabilities |> should contain "future.capability.v9"

        connection.Client.Close() |> Async.RunSynchronously
        backend.GetAwaiter().GetResult()
        scripted.KillCount |> should equal 0

    [<Fact>]
    member _.``a missing Workspace Explorer process returns one stable client failure``() =
        let factory _ = Error(FileNotFoundException "dotnet")

        let actual =
            RpcClient.startWith factory "/workspace/App.fsproj" |> Async.RunSynchronously

        match actual with
        | Error failure ->
            failure.Scope |> should equal BackendSessionFailure
            failure.Kind |> should equal BackendUnavailable

            failure.Message.Contains("FileNotFoundException", StringComparison.Ordinal)
            |> should equal false
        | Ok _ -> failwith "Expected the missing backend to fail."

    [<Fact>]
    member _.``backend diagnostics and credentials never escape an exited client failure``() =
        let scripted =
            ScriptedProcess(
                "password=secret https://user:secret@example.test token=also-secret "
                + String('x', 40000)
            )

        let factory _ = Ok(scripted :> IProcessHandle)
        scripted.Complete 19

        let actual =
            RpcClient.startWith factory "/workspace/App.fsproj" |> Async.RunSynchronously

        match actual with
        | Error failure ->
            failure.Kind |> should equal (BackendExited(Some 19))

            [ "secret"; "password"; "token"; "example.test" ]
            |> List.iter (fun value ->
                failure.Message.Contains(value, StringComparison.OrdinalIgnoreCase)
                |> should equal false)
        | Ok _ -> failwith "Expected the exited backend to fail."

    [<Fact>]
    member _.``negotiated frame limit rejects a later oversized backend response``() =
        let scripted = ScriptedProcess()
        let factory _ = Ok(scripted :> IProcessHandle)

        let backend =
            task {
                do! Script.initialize scripted 1024 [ "packages.sources.v1" ]
                let! messageId, _ = Script.request "package/sources" scripted

                let oversized =
                    RpcValue.map
                        [ "sources",
                          RpcValue.array
                              [ RpcValue.map
                                    [ "id", RpcValue.string "nuget.org"
                                      "name", RpcValue.string "nuget.org"
                                      "location", RpcValue.string (String('x', 2048))
                                      "availability", RpcValue.string "available" ] ] ]

                do!
                    Script.writeFrame
                        scripted.BackendOutput
                        (RpcFrame.Response(messageId, Ok oversized))

                scripted.Complete 0
            }

        let connection =
            RpcClient.startWith factory "/workspace/App.fsproj"
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        let target =
            SingleProject
                { Id = ProjectId "/workspace/App.fsproj"
                  Name = "App"
                  Frameworks = [ TargetFramework "net10.0" ] }

        let actual =
            connection.Client.Sources
                { Token = RequestToken 1L
                  Target = target }
            |> Async.RunSynchronously

        match actual with
        | Error failure -> failure.Kind |> should equal (Rejected "response_too_large")
        | Ok _ -> failwith "Expected an oversized response failure."

        connection.Client.Close() |> Async.RunSynchronously
        backend.GetAwaiter().GetResult()

    [<Fact>]
    member _.``incompatible initialization is typed and closes the owned backend``() =
        let scripted = ScriptedProcess()
        let factory _ = Ok(scripted :> IProcessHandle)

        let backend =
            task {
                let! initialize = Script.readFrame scripted.BackendInput

                match initialize with
                | RpcFrame.Request(messageId, "initialize", _) ->
                    do!
                        Script.writeFrame
                            scripted.BackendOutput
                            (RpcFrame.Response(messageId, Ok(Script.initializeResult 2 65536 [])))
                | actual -> failwith $"Expected initialize, got {actual}."

                do! Script.shutdown scripted true
            }

        let actual =
            RpcClient.startWith factory "/workspace/App.fsproj" |> Async.RunSynchronously

        match actual with
        | Error failure ->
            match failure.Kind with
            | BackendIncompatible _ -> ()
            | kind -> failwith $"Expected an incompatible backend, got {kind}."
        | Ok _ -> failwith "Expected initialization to fail."

        backend.GetAwaiter().GetResult()
        scripted.KillCount |> should equal 0

    [<Fact>]
    member _.``backend exit after a product request returns its typed exit failure``() =
        let scripted = ScriptedProcess()
        let factory _ = Ok(scripted :> IProcessHandle)

        let backend =
            task {
                do! Script.initialize scripted 65536 [ "packages.sources.v1" ]
                let! _, _ = Script.request "package/sources" scripted
                scripted.Complete 23
            }

        let connection =
            RpcClient.startWith factory "/workspace/App.fsproj"
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        let target =
            SingleProject
                { Id = ProjectId "/workspace/App.fsproj"
                  Name = "App"
                  Frameworks = [] }

        let actual =
            connection.Client.Sources
                { Token = RequestToken 2L
                  Target = target }
            |> Async.RunSynchronously

        match actual with
        | Error failure -> failure.Kind |> should equal (BackendExited(Some 23))
        | Ok _ -> failwith "Expected the backend exit to fail the request."

        connection.Client.Close() |> Async.RunSynchronously
        backend.GetAwaiter().GetResult()

    [<Fact>]
    member _.``stdout closing just before process exit retains the eventual exit code``() =
        let scripted = ScriptedProcess()
        let factory _ = Ok(scripted :> IProcessHandle)

        let backend =
            task {
                let! _ = Script.readFrame scripted.BackendInput
                scripted.CloseOutput()
                do! Task.Delay 50
                scripted.Exit 17
            }

        let actual =
            RpcClient.startWith factory "/workspace/App.fsproj" |> Async.RunSynchronously

        match actual with
        | Error failure -> failure.Kind |> should equal (BackendExited(Some 17))
        | Ok _ -> failwith "Expected initialization to observe the backend exit."

        backend.GetAwaiter().GetResult()

    [<Fact>]
    member _.``close kills an owned backend that acknowledges shutdown but does not exit``() =
        let scripted = ScriptedProcess()
        let factory _ = Ok(scripted :> IProcessHandle)

        let backend =
            task {
                do! Script.initialize scripted 65536 []
                do! Script.shutdown scripted false
            }

        let connection =
            RpcClient.startWith factory "/workspace/App.fsproj"
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        connection.Client.Close() |> Async.RunSynchronously
        backend.GetAwaiter().GetResult()
        scripted.KillCount |> should equal 1

    [<Fact>]
    member _.``stderr capture is bounded and redacts credentials before diagnostics are retained``
        ()
        =
        let diagnostic = "token=secret " + String('x', 40000)
        let text = ProcessDiagnostics.sanitize diagnostic

        text.Length
        |> should be (lessThanOrEqualTo ProcessDiagnostics.MaximumCharacters)

        text.Contains("secret", StringComparison.Ordinal) |> should equal false
        text.Contains("[redacted]", StringComparison.Ordinal) |> should equal true

    [<Fact>]
    member _.``invalid restore notifications never publish a successful refresh or completion``() =
        let scripted = ScriptedProcess()
        let factory _ = Ok(scripted :> IProcessHandle)

        let notificationsSent =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let backend =
            task {
                do! Script.initialize scripted 65536 [ "packages.installed.v1" ]
                let! messageId, fields = Script.request "package/installed" scripted
                let requestId = Script.requestIdentity fields

                do!
                    Script.writeFrame
                        scripted.BackendOutput
                        (RpcFrame.Response(
                            messageId,
                            Ok(Script.installedPage requestId "inProgress" [] None)
                        ))

                do!
                    Script.notify
                        scripted
                        "package/installed/refreshed"
                        [ "requestId", RpcValue.string (requestId.ToString "D")
                          "restore", RpcValue.string "inProgress"
                          "items", RpcValue.array [] ]

                do!
                    Script.notify
                        scripted
                        "package/restore/progress"
                        [ "requestId", RpcValue.string (requestId.ToString "D")
                          "state", RpcValue.string "unexpected" ]

                do!
                    Script.notify
                        scripted
                        "package/restore/completed"
                        [ "requestId", RpcValue.string (requestId.ToString "D")
                          "state", RpcValue.string "unexpected" ]

                notificationsSent.TrySetResult() |> ignore
                do! Script.shutdown scripted true
            }

        let connection =
            RpcClient.startWith factory "/workspace/App.fsproj"
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        let events = ResizeArray<PackageExplorerEvent>()
        use subscription = connection.Client.Subscribe events.Add

        let target =
            SingleProject
                { Id = ProjectId "/workspace/App.fsproj"
                  Name = "App"
                  Frameworks = [] }

        connection.Client.RefreshInstalled
            { Token = RequestToken 3L
              Target = target }
        |> Async.RunSynchronously
        |> Result.isOk
        |> should equal true

        notificationsSent.Task.GetAwaiter().GetResult()

        SpinWait.SpinUntil((fun () -> events.Count = 1), TimeSpan.FromSeconds 2.0)
        |> should equal true

        match events[0] with
        | RestoreCompleted(_, Error failure) ->
            failure.Kind |> should equal (Rejected "invalid_response")
        | actual -> failwith $"Expected one invalid restore completion, got {actual}."

        connection.Client.Close() |> Async.RunSynchronously
        backend.GetAwaiter().GetResult()

    [<Fact>]
    member _.``scripted package workflow preserves paging refresh readme batch and progress state``
        ()
        =
        let scripted = ScriptedProcess()
        let factory _ = Ok(scripted :> IProcessHandle)
        let package = "Example.Package"
        let project = "/workspace/App.fsproj"
        let secondProject = "/workspace/Lib.fsproj"
        let operationId = Guid.Parse "22222222-2222-2222-2222-222222222222"

        let backend =
            task {
                do!
                    Script.initialize
                        scripted
                        65536
                        [ "packages.search.v1"
                          "packages.details.v1"
                          "packages.readme.v1"
                          "packages.installed.v1"
                          "packages.restore.v1"
                          "packages.batch-preview.v1"
                          "packages.batch-execute.v1"
                          "packages.cancel.v1" ]

                let! firstSearchId, firstSearch = Script.request "package/search/start" scripted
                let firstRequestId = Script.requestIdentity firstSearch
                do! Script.accepted scripted firstSearchId firstRequestId

                do!
                    Script.notify
                        scripted
                        "package/search/completed"
                        [ "requestId", RpcValue.string (firstRequestId.ToString "D")
                          "result",
                          RpcValue.map
                              [ "requestId", RpcValue.string (firstRequestId.ToString "D")
                                "items",
                                RpcValue.array
                                    [ RpcValue.map
                                          [ "package", RpcValue.string package
                                            "version", RpcValue.string "2.0.0"
                                            "source", RpcValue.string "nuget.org" ] ]
                                "sourceFailures", RpcValue.array []
                                "continuation", RpcValue.string "next" ] ]

                let! secondSearchId, secondSearch = Script.request "package/search/start" scripted
                let secondRequestId = Script.requestIdentity secondSearch

                secondSearch
                |> RpcValue.requiredText "continuation"
                |> Result.defaultWith failwith
                |> should equal "next"

                do! Script.accepted scripted secondSearchId secondRequestId

                do!
                    Script.notify
                        scripted
                        "package/search/completed"
                        [ "requestId", RpcValue.string (secondRequestId.ToString "D")
                          "result",
                          RpcValue.map
                              [ "requestId", RpcValue.string (secondRequestId.ToString "D")
                                "items", RpcValue.array []
                                "sourceFailures", RpcValue.array [] ] ]

                let! installedId, firstInstalled = Script.request "package/installed" scripted
                let installedRequestId = Script.requestIdentity firstInstalled

                do!
                    Script.writeFrame
                        scripted.BackendOutput
                        (RpcFrame.Response(
                            installedId,
                            Ok(
                                Script.installedPage
                                    installedRequestId
                                    "inProgress"
                                    [ Script.installedItem project (Some "net10.0") package ]
                                    (Some "installed-next")
                            )
                        ))

                let! nextInstalledId, nextInstalled = Script.request "package/installed" scripted

                nextInstalled
                |> RpcValue.requiredText "continuation"
                |> Result.defaultWith failwith
                |> should equal "installed-next"

                do!
                    Script.writeFrame
                        scripted.BackendOutput
                        (RpcFrame.Response(
                            nextInstalledId,
                            Ok(
                                Script.installedPage
                                    installedRequestId
                                    "inProgress"
                                    [ Script.installedItem secondProject (Some "net9.0") package ]
                                    None
                            )
                        ))

                do!
                    Script.notify
                        scripted
                        "package/installed/refreshed"
                        [ "requestId", RpcValue.string (installedRequestId.ToString "D")
                          "restore", RpcValue.string "refreshed"
                          "items",
                          RpcValue.array
                              [ Script.installedItem project (Some "net10.0") package
                                Script.installedItem secondProject (Some "net9.0") package ] ]

                do!
                    Script.notify
                        scripted
                        "package/restore/completed"
                        [ "requestId", RpcValue.string (installedRequestId.ToString "D")
                          "state", RpcValue.string "refreshed" ]

                let! detailsId, _ = Script.request "package/details" scripted

                do!
                    Script.writeFrame
                        scripted.BackendOutput
                        (RpcFrame.Response(
                            detailsId,
                            Ok(
                                RpcValue.map
                                    [ "summary",
                                      RpcValue.map
                                          [ "package", RpcValue.string package
                                            "version", RpcValue.string "2.0.0"
                                            "source", RpcValue.string "nuget.org" ]
                                      "versions", RpcValue.array [ RpcValue.string "2.0.0" ]
                                      "authors", RpcValue.array [ RpcValue.string "ALSI" ]
                                      "dependencyGroups", RpcValue.array []
                                      "deprecation",
                                      RpcValue.map [ "kind", RpcValue.string "notDeprecated" ]
                                      "vulnerabilities", RpcValue.array []
                                      "readmeCommonMark", RpcValue.string "# Example" ]
                            )
                        ))

                let! previewId, _ = Script.request "package/previewBatch" scripted

                let target = Script.target project (Some "net10.0")

                let targetPreview =
                    RpcValue.map
                        [ "target", target
                          "change",
                          RpcValue.map
                              [ "kind", RpcValue.string "update"
                                "current",
                                RpcValue.map
                                    [ "kind", RpcValue.string "direct"
                                      "resolved", RpcValue.string "1.0.0" ]
                                "proposed",
                                RpcValue.map
                                    [ "kind", RpcValue.string "direct"
                                      "version", RpcValue.string "2.0.0" ] ]
                          "ownerFiles", RpcValue.array [ RpcValue.string project ]
                          "graphFreshness", RpcValue.string "current"
                          "impact", RpcValue.map [] ]

                do!
                    Script.writeFrame
                        scripted.BackendOutput
                        (RpcFrame.Response(
                            previewId,
                            Ok(
                                RpcValue.map
                                    [ "updates",
                                      RpcValue.array
                                          [ RpcValue.map
                                                [ "package", RpcValue.string package
                                                  "targetPreview", targetPreview ] ]
                                      "ownerFiles", RpcValue.array [ RpcValue.string project ]
                                      "workspaceRevision", RpcValue.string "revision-1"
                                      "fileFingerprints",
                                      RpcValue.array
                                          [ RpcValue.map
                                                [ "path", RpcValue.string project
                                                  "fingerprint", RpcValue.string "fingerprint-1" ] ]
                                      "confirmationToken", RpcValue.string "BATCH-TOKEN" ]
                            )
                        ))

                let! executeId, execute = Script.request "package/executeBatch/start" scripted
                let executeRequestId = Script.requestIdentity execute
                do! Script.accepted scripted executeId executeRequestId

                do!
                    Script.notify
                        scripted
                        "package/operations/progress"
                        [ "requestId", RpcValue.string (executeRequestId.ToString "D")
                          "progress",
                          RpcValue.map
                              [ "operationId", RpcValue.string (operationId.ToString "D")
                                "stage", RpcValue.string "applying"
                                "completed", RpcValue.integer 1
                                "total", RpcValue.integer 2 ] ]

                do!
                    Script.notify
                        scripted
                        "package/operations/completed"
                        [ "requestId", RpcValue.string (executeRequestId.ToString "D")
                          "result",
                          RpcValue.map
                              [ "operationId", RpcValue.string (operationId.ToString "D")
                                "entries",
                                RpcValue.array
                                    [ RpcValue.map
                                          [ "package", RpcValue.string package
                                            "target", target
                                            "state", RpcValue.string "completed" ] ]
                                "changedFiles", RpcValue.array [ RpcValue.string project ]
                                "restore", RpcValue.string "completed" ] ]

                let! refreshedId, refreshed = Script.request "package/installed" scripted
                let refreshedRequestId = Script.requestIdentity refreshed

                do!
                    Script.writeFrame
                        scripted.BackendOutput
                        (RpcFrame.Response(
                            refreshedId,
                            Ok(
                                Script.installedPage
                                    refreshedRequestId
                                    "refreshed"
                                    [ Script.installedItem project (Some "net10.0") package ]
                                    None
                            )
                        ))

                do! Script.shutdown scripted true
            }

        let connection =
            RpcClient.startWith factory "/workspace/App.fsproj"
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        let target =
            SingleProject
                { Id = ProjectId project
                  Name = "App"
                  Frameworks = [ TargetFramework "net10.0" ] }

        let events = ResizeArray<PackageExplorerEvent>()
        use subscription = connection.Client.Subscribe events.Add

        let query page =
            { Text = "example"
              IncludePrerelease = false
              Page = page
              PageSize = 25 }

        let firstPage =
            connection.Client.Search
                { Token = RequestToken 10L
                  Target = target
                  Source = None
                  Query = query 0 }
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        firstPage.HasNextPage |> should equal true
        firstPage.Packages.Head.Id |> should equal (PackageId package)

        let secondPage =
            connection.Client.Search
                { Token = RequestToken 11L
                  Target = target
                  Source = None
                  Query = query 1 }
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        secondPage.HasNextPage |> should equal false

        let immediate =
            connection.Client.RefreshInstalled
                { Token = RequestToken 12L
                  Target = target }
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        immediate.Items.Length |> should equal 2

        immediate.Items
        |> List.map _.Target.Project
        |> should equal [ ProjectId project; ProjectId secondProject ]

        let readme =
            connection.Client.GetReadme
                { Token = RequestToken 13L
                  Target = target
                  Package = PackageId package }
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        readme.CommonMark |> should equal "# Example"

        let preview =
            connection.Client.Preview
                { Token = RequestToken 14L
                  Target = target
                  Selection =
                    { Projects = Set.empty
                      Frameworks = Map.empty }
                  Operation =
                    UpdateSelectedPackages(set [ PackageId package; PackageId "Second.Package" ]) }
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        preview.Id |> should equal (PreviewId "BATCH-TOKEN")

        let applied =
            connection.Client.Apply
                { Token = RequestToken 15L
                  Target = target
                  Preview = preview.Id }
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        applied.Installed.Items.Length |> should equal 1

        events
        |> Seq.exists (function
            | InstalledRefreshed(RequestToken 12L, snapshot) -> snapshot.Items.Length = 2
            | _ -> false)
        |> should equal true

        events
        |> Seq.exists (function
            | OperationProgressed(RequestToken 15L,
                                  { Operation = OperationId operation
                                    Completed = 1
                                    Total = 2 }) -> operation = operationId.ToString "D"
            | _ -> false)
        |> should equal true

        connection.Client.Close() |> Async.RunSynchronously
        backend.GetAwaiter().GetResult()

    [<Fact>]
    member _.``cancellation completes the active request and suppresses a late success``() =
        let scripted = ScriptedProcess()
        let factory _ = Ok(scripted :> IProcessHandle)

        let accepted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let backend =
            task {
                do! Script.initialize scripted 65536 [ "packages.search.v1"; "packages.cancel.v1" ]
                let! messageId, fields = Script.request "package/search/start" scripted
                let requestId = Script.requestIdentity fields
                do! Script.accepted scripted messageId requestId
                accepted.TrySetResult() |> ignore

                let! cancelId, cancelFields = Script.request "package/cancel" scripted

                cancelFields
                |> RpcValue.requiredText "requestId"
                |> Result.defaultWith failwith
                |> Guid.Parse
                |> should equal requestId

                do! Script.acknowledged scripted cancelId

                do!
                    Script.notify
                        scripted
                        "package/search/completed"
                        [ "requestId", RpcValue.string (requestId.ToString "D")
                          "result",
                          RpcValue.map
                              [ "requestId", RpcValue.string (requestId.ToString "D")
                                "items", RpcValue.array []
                                "sourceFailures", RpcValue.array [] ] ]

                do! Script.shutdown scripted true
            }

        let connection =
            RpcClient.startWith factory "/workspace/App.fsproj"
            |> Async.RunSynchronously
            |> Result.defaultWith (fun failure -> failwith failure.Message)

        let target =
            SingleProject
                { Id = ProjectId "/workspace/App.fsproj"
                  Name = "App"
                  Frameworks = [] }

        let token = RequestToken 4L

        let search =
            connection.Client.Search
                { Token = token
                  Target = target
                  Source = None
                  Query =
                    { Text = "example"
                      IncludePrerelease = false
                      Page = 0
                      PageSize = 25 } }
            |> Async.StartAsTask

        accepted.Task.GetAwaiter().GetResult()

        connection.Client.Cancel token
        |> Async.RunSynchronously
        |> Result.isOk
        |> should equal true

        match search.GetAwaiter().GetResult() with
        | Error failure -> failure.Kind |> should equal Cancelled
        | Ok _ -> failwith "Expected the cancelled search to stay cancelled."

        connection.Client.Close() |> Async.RunSynchronously
        backend.GetAwaiter().GetResult()
