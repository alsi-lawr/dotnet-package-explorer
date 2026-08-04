namespace Dotnet.PackageExplorer.Terminal

open System
open System.IO

module Program =
    [<Literal>]
    let NonInteractiveDiagnostic =
        "dotnet-package-explorer requires an interactive terminal."

    [<EntryPoint>]
    let main arguments =
        if Console.IsInputRedirected || Console.IsOutputRedirected then
            Console.Error.WriteLine NonInteractiveDiagnostic
            2
        else
            let target =
                arguments
                |> Array.tryHead
                |> Option.orElseWith (fun () -> Some(Directory.GetCurrentDirectory()))
                |> Option.defaultValue "."

            let getEnvironment name =
                Environment.GetEnvironmentVariable name
                |> Option.ofObj
                |> Option.defaultValue ""

            let profile = Theme.detect getEnvironment
            Runtime.run profile target
