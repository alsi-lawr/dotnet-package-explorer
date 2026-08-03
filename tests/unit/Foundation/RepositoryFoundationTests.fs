namespace Dotnet.PackageExplorer.Foundation.UnitTests

open System
open System.IO
open FsUnit.Xunit
open Xunit

[<Sealed>]
type RepositoryFoundationTests() =
    [<Fact>]
    member _.``terminal project graph makes each concern assembly available to its consumer``() =
        [ "Dotnet.PackageExplorer.Application.dll"
          "Dotnet.PackageExplorer.RpcClient.dll"
          "Dotnet.PackageExplorer.Terminal.dll" ]
        |> List.map (fun assemblyName -> Path.Combine(AppContext.BaseDirectory, assemblyName))
        |> List.iter (fun assemblyPath -> File.Exists assemblyPath |> should equal true)
