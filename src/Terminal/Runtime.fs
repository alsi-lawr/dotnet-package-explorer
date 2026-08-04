namespace Dotnet.PackageExplorer.Terminal

open System
open System.IO
open System.Threading
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open Terminal.Gui.App

[<RequireQualifiedAccess>]
module internal Effects =
    let start
        (client: PackageExplorerClient)
        (dispatch: Message -> unit)
        (cancellationToken: CancellationToken)
        effect
        =
        let operation =
            async {
                match effect with
                | SearchPackages request ->
                    let! result = client.Search request
                    dispatch (SearchCompleted(request.Token, result))
                | RefreshInstalled request ->
                    let! result = client.RefreshInstalled request
                    dispatch (RefreshCompleted(request.Token, result))
                | FindPackageUpdates request ->
                    let! result = client.FindUpdates request
                    dispatch (UpdatesCompleted(request.Token, result))
                | FindPackageConsolidation request ->
                    let! result = client.FindConsolidation request
                    dispatch (ConsolidationCompleted(request.Token, result))
                | GetPackageDetails request ->
                    let! result = client.GetDetails request
                    dispatch (DetailsCompleted(request.Token, request.Package, result))
                | GetPackageReadme request ->
                    let! result = client.GetReadme request
                    dispatch (ReadmeCompleted(request.Token, request.Package, result))
                | PreviewOperation request ->
                    let! result = client.Preview request
                    dispatch (PreviewCompleted(request.Token, result))
                | ApplyOperation request ->
                    let! result = client.Apply request
                    dispatch (ApplyCompleted(request.Token, result))
                | CancelRequest token ->
                    let! _ = client.Cancel token
                    ()
            }

        Async.Start(operation, cancellationToken)

type internal TerminalLifetime
    (client: PackageExplorerClient, ui: IDisposable, cancellation: CancellationTokenSource) =
    let mutable closed = 0

    member _.Close() =
        async {
            if Interlocked.Exchange(&closed, 1) = 0 then
                let mutable failure: exn option = None

                let capture action =
                    try
                        action ()
                    with error ->
                        if failure.IsNone then
                            failure <- Some error

                capture cancellation.Cancel
                capture ui.Dispose

                try
                    do! client.Close()
                with error ->
                    if failure.IsNone then
                        failure <- Some error

                capture cancellation.Dispose

                match failure with
                | Some error -> return raise error
                | None -> ()
        }

[<RequireQualifiedAccess>]
module internal Runtime =
    let private eventMessage =
        function
        | InstalledRefreshed(token, snapshot) -> Some(RefreshCompleted(token, Ok snapshot))
        | OperationProgressed(token, progress) -> Some(ApplyProgressed(token, progress))
        | RestoreProgressed _
        | RestoreCompleted _ -> None

    let target (path: string) =
        let name =
            Path.GetFileNameWithoutExtension path
            |> Option.ofObj
            |> Option.defaultValue path

        let project =
            { Id = ProjectId path
              Name = name
              Frameworks = [] }

        let extension = Path.GetExtension path |> Option.ofObj |> Option.defaultValue ""

        match extension.ToLowerInvariant() with
        | ".sln"
        | ".slnx" -> Solution(path, [])
        | ".csproj"
        | ".fsproj"
        | ".vbproj" -> SingleProject project
        | _ -> Workspace(path, [])

    let runConnected
        (createApplication: unit -> IApplication)
        driverName
        profile
        target
        (connection: RpcConnection)
        =
        let cancellation = new CancellationTokenSource()
        let mutable subscription: IDisposable option = None

        let ui =
            { new IDisposable with
                member _.Dispose() = subscription |> Option.iter _.Dispose() }

        let lifetime = TerminalLifetime(connection.Client, ui, cancellation)

        try
            use application = createApplication().Init driverName
            let initial, initialEffects = Model.create target connection.Capabilities None
            let mutable current = initial
            let mutable window: ExplorerWindow option = None

            let rec dispatch message =
                application.Invoke(
                    Action(fun () ->
                        let next, effects = Update.update message current
                        current <- next
                        window |> Option.iter (fun view -> view.Render next)

                        effects
                        |> List.iter (Effects.start connection.Client dispatch cancellation.Token))
                )

            use view =
                new ExplorerWindow(
                    initial,
                    dispatch,
                    (fun () -> application.RequestStop()),
                    profile
                )

            window <- Some view
            use _keyboard = view.BindKeyboard application.Keyboard

            subscription <-
                Some(
                    connection.Client.Subscribe(fun event ->
                        event |> eventMessage |> Option.iter dispatch)
                )

            initialEffects
            |> List.iter (Effects.start connection.Client dispatch cancellation.Token)

            application.Run view |> ignore
            0
        finally
            lifetime.Close() |> Async.RunSynchronously

    let run profile path =
        let target = target path

        match RpcClient.connect path |> Async.RunSynchronously with
        | Error failure ->
            Console.Error.WriteLine $"Package Explorer could not start: {failure.Message}"
            1
        | Ok connection ->
            runConnected (fun () -> Application.Create()) null profile target connection
