namespace Dotnet.PackageExplorer.RpcClient

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type internal TransportFailure =
    | Unavailable
    | Exited of int option
    | MalformedFrame
    | OversizedFrame
    | TruncatedFrame
    | Cancelled

type internal IProcessHandle =
    inherit IAsyncDisposable

    abstract Input: Stream
    abstract Output: Stream
    abstract Error: TextReader
    abstract HasExited: bool
    abstract ExitCode: int option
    abstract WaitForExitAsync: CancellationToken -> Task
    abstract KillTree: unit -> unit

type private SystemProcessHandle(handle: Process) =
    interface IProcessHandle with
        member _.Input = handle.StandardInput.BaseStream
        member _.Output = handle.StandardOutput.BaseStream
        member _.Error = handle.StandardError
        member _.HasExited = handle.HasExited

        member _.ExitCode = if handle.HasExited then Some handle.ExitCode else None

        member _.WaitForExitAsync cancellationToken =
            handle.WaitForExitAsync cancellationToken

        member _.KillTree() =
            if not handle.HasExited then
                try
                    handle.Kill(true)
                with :? InvalidOperationException ->
                    ()

        member _.DisposeAsync() =
            handle.Dispose()
            ValueTask.CompletedTask

type internal ProcessFactory = ProcessStartInfo -> Result<IProcessHandle, exn>

[<RequireQualifiedAccess>]
module internal ProcessFactory =
    let system (startInfo: ProcessStartInfo) : Result<IProcessHandle, exn> =
        try
            let handle = new Process(StartInfo = startInfo)

            if handle.Start() then
                Ok(SystemProcessHandle handle)
            else
                handle.Dispose()
                Error(InvalidOperationException "The Workspace Explorer handle did not start.")
        with error ->
            Error error

[<RequireQualifiedAccess>]
module internal ProcessLaunch =
    let create target =
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "dotnet"
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.ArgumentList.Add "we"
        startInfo.ArgumentList.Add "packages"
        startInfo.ArgumentList.Add target
        startInfo.ArgumentList.Add "--pipe"
        startInfo

[<RequireQualifiedAccess>]
module internal ProcessDiagnostics =
    [<Literal>]
    let MaximumCharacters = 32768

    let sanitize (value: string) =
        value
        |> Seq.truncate MaximumCharacters
        |> Seq.filter (fun character ->
            character = '\n'
            || character = '\r'
            || character = '\t'
            || (character >= ' ' && character <= '~'))
        |> Array.ofSeq
        |> String
        |> fun text ->
            Regex.Replace(
                text,
                @"(?i)(password|passwd|token|apikey|api_key|username)\s*[:=]\s*[^\s;&]+",
                "$1=[redacted]"
            )
        |> fun text -> Regex.Replace(text, @"(?i)(https?://)[^/\s:@]+:[^/\s@]+@", "$1[redacted]@")
        |> Seq.truncate MaximumCharacters
        |> Array.ofSeq
        |> String

type internal NotificationWait internal (task: Task<Result<RpcValue, RpcError>>) =
    member _.Task = task

type internal ProcessSession private (handle: IProcessHandle) =
    let responses =
        ConcurrentDictionary<uint32, TaskCompletionSource<Result<RpcValue, RpcError>>>()

    let notifications =
        ConcurrentDictionary<string * string, TaskCompletionSource<Result<RpcValue, RpcError>>>()

    let lifetime = new CancellationTokenSource()
    let writeLock = new SemaphoreSlim(1, 1)
    let receivedNotification = Event<string * RpcValue>()
    let mutable nextMessageId = 0
    let mutable closeStarted = 0
    let mutable maximumFrameBytes = Protocol.MaximumFrameBytes
    let mutable terminalFailure: TransportFailure option = None
    let mutable diagnostics = StringBuilder()

    let newCompletion () =
        TaskCompletionSource<Result<RpcValue, RpcError>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        )

    let tryRpcError value =
        match value with
        | RpcValue.Map fields ->
            match RpcValue.requiredText "code" fields, RpcValue.requiredText "message" fields with
            | Ok code, Ok message ->
                Some
                    { Code = code
                      Message = message
                      Data = Map.tryFind "data" fields }
            | _ -> None
        | _ -> None

    let failAll failure =
        if terminalFailure.IsNone then
            terminalFailure <- Some failure
            lifetime.Cancel()

            for KeyValue(_, completion) in responses do
                completion.TrySetCanceled() |> ignore

            for KeyValue(_, completion) in notifications do
                completion.TrySetCanceled() |> ignore

            responses.Clear()
            notifications.Clear()

    let dispatch frame =
        match frame with
        | RpcFrame.Response(messageId, outcome) ->
            match responses.TryRemove messageId with
            | true, completion -> completion.TrySetResult outcome |> ignore
            | _ -> ()
        | RpcFrame.Notification(methodName, (RpcValue.Map fields as parameters)) ->
            match RpcValue.optionalText "requestId" fields with
            | Ok(Some requestId) ->
                match notifications.TryRemove((methodName, requestId)) with
                | true, completion ->
                    let outcome =
                        match Map.tryFind "error" fields with
                        | Some error ->
                            match tryRpcError error with
                            | Some failure -> Error failure
                            | None ->
                                Error
                                    { Code = "invalid_response"
                                      Message = "The backend returned an invalid error."
                                      Data = None }
                        | None ->
                            match Map.tryFind "result" fields with
                            | Some result -> Ok result
                            | None -> Ok parameters

                    completion.TrySetResult outcome |> ignore
                | _ -> ()
            | _ -> ()

            receivedNotification.Trigger(methodName, parameters)
        | RpcFrame.Notification _
        | RpcFrame.Request _ -> ()

    let exitCodeAfterOutputClosed () =
        task {
            if not handle.HasExited then
                try
                    use grace = new CancellationTokenSource(TimeSpan.FromMilliseconds 250.0)
                    do! handle.WaitForExitAsync grace.Token
                with :? OperationCanceledException ->
                    ()

            return handle.ExitCode
        }

    let outputPump =
        task {
            let buffer = ResizeArray<byte>()
            let readBuffer = Array.zeroCreate<byte> 8192
            let mutable keepReading = true

            try
                while keepReading && not lifetime.IsCancellationRequested do
                    let! read =
                        handle.Output.ReadAsync(readBuffer.AsMemory(), lifetime.Token).AsTask()

                    if read = 0 then
                        keepReading <- false
                    else
                        for index in 0 .. read - 1 do
                            buffer.Add readBuffer[index]

                        let mutable parse = true

                        while parse && buffer.Count > 0 do
                            let bytes = buffer.ToArray()
                            let frameLimit = Volatile.Read(&maximumFrameBytes)

                            match
                                MessagePackCodec.tryReadFrameWithLimit
                                    frameLimit
                                    (ReadOnlyMemory<byte> bytes)
                            with
                            | Ok(frame, consumed) ->
                                dispatch frame
                                buffer.RemoveRange(0, consumed)
                            | Error DecodeFailure.Incomplete -> parse <- false
                            | Error DecodeFailure.TooLarge ->
                                parse <- false
                                keepReading <- false
                                failAll TransportFailure.OversizedFrame
                            | Error(DecodeFailure.Invalid _) ->
                                parse <- false
                                keepReading <- false
                                failAll TransportFailure.MalformedFrame

                if
                    Interlocked.CompareExchange(&closeStarted, 0, 0) = 0 && terminalFailure.IsNone
                then
                    if buffer.Count = 0 then
                        let! exitCode = exitCodeAfterOutputClosed ()
                        failAll (TransportFailure.Exited exitCode)
                    else
                        failAll TransportFailure.TruncatedFrame
            with
            | :? OperationCanceledException -> ()
            | _ when Interlocked.CompareExchange(&closeStarted, 0, 0) <> 0 -> ()
            | _ -> failAll TransportFailure.MalformedFrame
        }

    let errorPump =
        task {
            let buffer = Array.zeroCreate<char> 2048
            let mutable keepReading = true

            try
                while keepReading && not lifetime.IsCancellationRequested do
                    let! read = handle.Error.ReadAsync(buffer.AsMemory(), lifetime.Token).AsTask()

                    if read = 0 then
                        keepReading <- false
                    elif diagnostics.Length < ProcessDiagnostics.MaximumCharacters then
                        let available = ProcessDiagnostics.MaximumCharacters - diagnostics.Length
                        diagnostics.Append(buffer, 0, min read available) |> ignore
            with
            | :? OperationCanceledException -> ()
            | _ -> ()

            diagnostics <- StringBuilder(ProcessDiagnostics.sanitize (diagnostics.ToString()))
        }

    let handleExit =
        task {
            try
                do! handle.WaitForExitAsync lifetime.Token
            with :? OperationCanceledException ->
                ()
        }

    member private _.Failure = terminalFailure
    member internal _.TerminalFailure = terminalFailure

    member _.Diagnostics =
        let value = diagnostics.ToString()

        if String.IsNullOrWhiteSpace value then None else Some value

    member _.Notifications = receivedNotification.Publish

    member _.UseMaximumFrameBytes(value) =
        if value < 1024 || value > Protocol.MaximumFrameBytes then
            invalidArg "value" "The negotiated frame limit is outside the secure profile."

        Volatile.Write(&maximumFrameBytes, value)

    member _.PrepareNotification(methodName: string, requestId: Guid) =
        let key = methodName, requestId.ToString "D"
        let completion = newCompletion ()

        if notifications.TryAdd(key, completion) then
            NotificationWait completion.Task
        else
            invalidOp "A notification wait is already registered for this request."

    member _.ForgetNotification(methodName: string, requestId: Guid) =
        match notifications.TryRemove((methodName, requestId.ToString "D")) with
        | true, completion -> completion.TrySetCanceled() |> ignore
        | _ -> ()

    member this.SendAsync
        (methodName: string, parameters: RpcValue, cancellationToken: CancellationToken)
        =
        task {
            match this.Failure with
            | Some failure -> return Error(Choice1Of2 failure)
            | None ->
                let messageId = uint32 (Interlocked.Increment(&nextMessageId))
                let completion = newCompletion ()

                if not (responses.TryAdd(messageId, completion)) then
                    return Error(Choice1Of2 TransportFailure.MalformedFrame)
                else
                    try
                        do! writeLock.WaitAsync cancellationToken

                        try
                            let frame =
                                RpcFrame.Request(messageId, methodName, parameters)
                                |> MessagePackCodec.encode

                            do!
                                handle.Input
                                    .WriteAsync(frame.AsMemory(), cancellationToken)
                                    .AsTask()

                            do! handle.Input.FlushAsync cancellationToken
                        finally
                            writeLock.Release() |> ignore

                        let! outcome = completion.Task.WaitAsync cancellationToken

                        return
                            match outcome with
                            | Ok result -> Ok result
                            | Error error -> Error(Choice2Of2 error)
                    with
                    | :? OperationCanceledException ->
                        responses.TryRemove messageId |> ignore

                        return
                            Error(
                                Choice1Of2(
                                    if cancellationToken.IsCancellationRequested then
                                        TransportFailure.Cancelled
                                    else
                                        this.Failure
                                        |> Option.defaultValue (
                                            TransportFailure.Exited handle.ExitCode
                                        )
                                )
                            )
                    | _ ->
                        responses.TryRemove messageId |> ignore

                        return
                            Error(
                                Choice1Of2(
                                    this.Failure
                                    |> Option.defaultValue (TransportFailure.Exited handle.ExitCode)
                                )
                            )
        }

    member this.CloseAsync() =
        task {
            if Interlocked.Exchange(&closeStarted, 1) = 0 then
                if this.Failure.IsNone && not handle.HasExited then
                    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 2.0)
                    let! _ = this.SendAsync("shutdown", RpcValue.map [], timeout.Token)

                    if not handle.HasExited then
                        try
                            use exitGrace =
                                new CancellationTokenSource(TimeSpan.FromMilliseconds 500.0)

                            do! handle.WaitForExitAsync exitGrace.Token
                        with :? OperationCanceledException ->
                            ()

                lifetime.Cancel()

                try
                    handle.Input.Close()
                with _ ->
                    ()

                if not handle.HasExited then
                    handle.KillTree()

                try
                    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 2.0)
                    do! handle.WaitForExitAsync timeout.Token
                with _ ->
                    ()

                let! _ = Task.WhenAll(outputPump, errorPump, handleExit)
                do! handle.DisposeAsync().AsTask()
                writeLock.Dispose()
                lifetime.Dispose()
        }

    static member Start(factory, target) =
        match factory (ProcessLaunch.create target) with
        | Ok handle -> Ok(ProcessSession handle)
        | Error _ -> Error TransportFailure.Unavailable
