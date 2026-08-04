namespace Dotnet.PackageExplorer.Terminal.UnitTests

open System
open System.Threading
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open Dotnet.PackageExplorer.Terminal
open FsUnit.Xunit
open Terminal.Gui.App
open Xunit

[<Sealed>]
type RuntimeTests() =
    let success value = async { return Ok value }

    let client close subscribe =
        { Sources = fun _ -> success []
          SourceMapping =
            fun request ->
                success
                    { Kind = ApplyAllowed
                      Package = Some request.Package
                      Sources = [] }
          Search =
            fun request ->
                success
                    { Query = request.Query
                      Packages = []
                      HasNextPage = false }
          RefreshInstalled =
            fun _ ->
                success
                    { Items = []
                      CapturedAt = DateTimeOffset.UnixEpoch }
          FindUpdates = fun _ -> success { Updates = []; Continuation = None }
          FindConsolidation = fun _ -> success { Packages = []; Continuation = None }
          GetDetails =
            fun request ->
                success
                    { Package =
                        { Id = request.Package
                          DisplayName = "Package"
                          InstalledVersion = None
                          LatestVersion = None
                          Kind = None
                          Source = None
                          Relevance = None
                          Description = None }
                      Versions = []
                      Dependencies = []
                      IsDeprecated = false
                      Vulnerabilities = []
                      License = None }
          GetReadme =
            fun request ->
                success
                    { Package = request.Package
                      CommonMark = "" }
          Preview =
            fun request ->
                success
                    { Id = PreviewId "preview"
                      Operation = request.Operation
                      Summary = []
                      Projects = []
                      Dependencies = []
                      Files = [] }
          Apply =
            fun request ->
                success
                    { Preview = request.Preview
                      Installed =
                        { Items = []
                          CapturedAt = DateTimeOffset.UnixEpoch }
                      Summary = "" }
          Cancel = fun _ -> success ()
          Subscribe = subscribe
          Close = close }

    [<Fact>]
    member _.``terminal lifetime closes the client subscription and UI cancellation exactly once``
        ()
        =
        let mutable closeCount = 0
        let mutable disposeCount = 0
        let cancellation = new CancellationTokenSource()

        let subscription =
            { new IDisposable with
                member _.Dispose() = disposeCount <- disposeCount + 1 }

        let packageClient =
            client (fun () -> async { closeCount <- closeCount + 1 }) (fun _ -> subscription)

        let lifetime = TerminalLifetime(packageClient, subscription, cancellation)
        lifetime.Close() |> Async.RunSynchronously
        lifetime.Close() |> Async.RunSynchronously

        closeCount |> should equal 1
        disposeCount |> should equal 1

    [<Fact>]
    member _.``terminal lifetime disposes cancellation and UI when client close fails exactly once``
        ()
        =
        let mutable closeCount = 0
        let mutable disposeCount = 0
        let mutable cancelled = false
        let cancellation = new CancellationTokenSource()
        use _registration = cancellation.Token.Register(fun () -> cancelled <- true)

        let subscription =
            { new IDisposable with
                member _.Dispose() = disposeCount <- disposeCount + 1 }

        let packageClient =
            client
                (fun () ->
                    async {
                        closeCount <- closeCount + 1
                        return raise (InvalidOperationException "close failed")
                    })
                (fun _ -> subscription)

        let lifetime = TerminalLifetime(packageClient, subscription, cancellation)

        (fun () -> lifetime.Close() |> Async.RunSynchronously)
        |> should throw typeof<InvalidOperationException>

        lifetime.Close() |> Async.RunSynchronously

        closeCount |> should equal 1
        disposeCount |> should equal 1
        cancelled |> should equal true

        (fun () -> cancellation.Token |> ignore)
        |> should throw typeof<ObjectDisposedException>

    [<Fact>]
    member _.``connected composition closes the client and subscription once after an ANSI run``() =
        let mutable closeCount = 0
        let mutable disposeCount = 0

        let subscription =
            { new IDisposable with
                member _.Dispose() = disposeCount <- disposeCount + 1 }

        let packageClient =
            client (fun () -> async { closeCount <- closeCount + 1 }) (fun _ -> subscription)

        let connection =
            { Client = packageClient
              Capabilities = Set.empty
              ServerCapabilities = Set.empty }

        let createApplication () =
            let application = Application.Create()
            application.StopAfterFirstIteration <- true
            application

        Runtime.runConnected
            createApplication
            "ansi"
            Ansi16
            (Runtime.target "App.fsproj")
            connection
        |> should equal 0

        closeCount |> should equal 1
        disposeCount |> should equal 1

    [<Fact>]
    member _.``connected composition closes the client when Terminal Gui initialization fails``() =
        let mutable closeCount = 0

        let packageClient =
            client (fun () -> async { closeCount <- closeCount + 1 }) (fun _ ->
                { new IDisposable with
                    member _.Dispose() = () })

        let connection =
            { Client = packageClient
              Capabilities = Set.empty
              ServerCapabilities = Set.empty }

        let start () =
            Runtime.runConnected
                (fun () -> raise (InvalidOperationException "UI failed"))
                "ansi"
                Ansi16
                (Runtime.target "App.fsproj")
                connection
            |> ignore

        start |> should throw typeof<InvalidOperationException>
        closeCount |> should equal 1

    [<Fact>]
    member _.``target routing distinguishes solutions projects and workspace directories``() =
        [ "Example.slnx", Solution("Example.slnx", [])
          "App.fsproj",
          SingleProject
              { Id = ProjectId "App.fsproj"
                Name = "App"
                Frameworks = [] }
          "src", Workspace("src", []) ]
        |> List.iter (fun (path, expected) -> Runtime.target path |> should equal expected)

    [<Fact>]
    member _.``non-interactive startup diagnostic remains plain stable and bounded``() =
        Program.NonInteractiveDiagnostic
        |> should equal "dotnet-package-explorer requires an interactive terminal."

        Program.NonInteractiveDiagnostic.Length |> should be (lessThan 100)
