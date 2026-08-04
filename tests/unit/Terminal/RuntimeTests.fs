namespace Dotnet.PackageExplorer.Terminal.UnitTests

open System
open System.Threading
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open Dotnet.PackageExplorer.Terminal
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type RuntimeTests() =
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
        |> should equal "dotnet-pe requires an interactive terminal."

        Program.NonInteractiveDiagnostic.Length |> should be (lessThan 100)
