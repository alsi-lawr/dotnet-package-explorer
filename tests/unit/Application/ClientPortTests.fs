namespace Dotnet.PackageExplorer.Application.UnitTests

open Dotnet.PackageExplorer.Application
open FsUnit.Xunit
open TestData
open Xunit

[<Sealed>]
type ClientPortTests() =
    [<Fact>]
    member _.``the application-owned client port can be implemented with pure F# results``() =
        let client =
            { Search =
                fun request ->
                    async {
                        return
                            Ok
                                { Query = request.Query
                                  Packages = []
                                  HasNextPage = false }
                    }
              RefreshInstalled = fun _ -> async { return Ok(snapshot [ directPackage ]) }
              GetDetails = fun _ -> async { return Ok(details directPackage) }
              GetReadme =
                fun request ->
                    async {
                        return
                            Ok
                                { Package = request.Package
                                  CommonMark = "# Package" }
                    }
              Preview = fun request -> async { return Ok(preview "preview" request.Operation) }
              Apply =
                fun request ->
                    async {
                        return
                            Ok
                                { Preview = request.Preview
                                  Installed = snapshot [ directPackage ]
                                  Summary = "Applied" }
                    }
              Cancel = fun _ -> async { return Ok() }
              Close = fun () -> async { return () } }

        let query =
            { Text = "json"
              IncludePrerelease = false
              Page = 0
              PageSize = 50 }

        let result =
            client.Search
                { Token = RequestToken 1L
                  Target = solution
                  Source = None
                  Query = query }
            |> Async.RunSynchronously

        match result with
        | Ok page ->
            page.Query |> should equal query
            page.Packages |> should be Empty
            page.HasNextPage |> should equal false
        | Error clientFailure -> failwith $"The pure client returned {clientFailure}."

        client.Close() |> Async.RunSynchronously
