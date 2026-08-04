namespace Dotnet.PackageExplorer.RpcClient.UnitTests

open System
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open FsUnit.Xunit
open Xunit

[<Sealed>]
type ProtocolTests() =
    let fixtureRequestId = Guid.Parse "11111111-1111-1111-1111-111111111111"

    [<Fact>]
    member _.``malformed oversized and truncated frames remain typed framing failures``() =
        match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte> [| 0xc1uy |]) with
        | Error(DecodeFailure.Invalid _) -> ()
        | actual -> failwith $"Expected malformed input, got {actual}."

        let oversized = Array.zeroCreate<byte> (Protocol.MaximumFrameBytes + 1)
        oversized[0] <- 0xdbuy
        oversized[1] <- 0x01uy

        match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte> oversized) with
        | Error DecodeFailure.TooLarge -> ()
        | actual -> failwith $"Expected oversized input, got {actual}."

        let complete =
            RpcFrame.Response(1u, Ok(RpcValue.map [ "accepted", RpcValue.Boolean true ]))
            |> MessagePackCodec.encode

        let truncated = complete[0 .. complete.Length - 2]

        match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte> truncated) with
        | Error DecodeFailure.Incomplete -> ()
        | actual -> failwith $"Expected truncated input, got {actual}."

    [<Fact>]
    member _.``core collection limits accept their boundary headers and reject larger counts``() =
        let collectionHeader code count =
            [| code
               byte (count >>> 24)
               byte (count >>> 16)
               byte (count >>> 8)
               byte count |]

        let assertBoundary code maximum valuesPerItem =
            let boundary = collectionHeader code maximum

            match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte> boundary) with
            | Error DecodeFailure.Incomplete -> ()
            | actual -> failwith $"Expected boundary collection to remain incomplete, got {actual}."

            let count = maximum + 1
            let excessive = Array.zeroCreate<byte> <| 5 + count * valuesPerItem
            Array.Copy(collectionHeader code count, excessive, 5)
            excessive.AsSpan(5).Fill 0xc0uy

            match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte> excessive) with
            | Error(DecodeFailure.Invalid _) -> ()
            | actual -> failwithf "Expected excessive collection to be invalid, got %A." actual

        assertBoundary 0xdduy Protocol.MaximumArrayItems 1
        assertBoundary 0xdfuy Protocol.MaximumMapItems 2

    [<Fact>]
    member _.``malformed protocol enums and incomplete initialization return decode failures``() =
        let malformedSources =
            RpcValue.map
                [ "sources",
                  RpcValue.array
                      [ RpcValue.map
                            [ "id", RpcValue.string "nuget.org"
                              "name", RpcValue.string "nuget.org"
                              "location", RpcValue.string "https://example.test"
                              "availability", RpcValue.string "unexpected" ] ] ]

        match PackageMapping.decodeSources malformedSources with
        | Error _ -> ()
        | Ok _ -> failwith "Expected the malformed availability to fail."

        let incompleteInitialize =
            RpcValue.map
                [ "protocolVersion",
                  RpcValue.map [ "major", RpcValue.integer 1; "minor", RpcValue.integer 0 ]
                  "capabilities", RpcValue.array [] ]

        match PackageMapping.decodeInitialize incompleteInitialize with
        | Error _ -> ()
        | Ok _ -> failwith "Expected incomplete initialization to fail."

    [<Fact>]
    member _.``installed pages retain duplicate package ids on distinct project frameworks``() =
        let target project framework =
            RpcValue.map
                [ "project", RpcValue.string project; "framework", RpcValue.string framework ]

        let item project framework =
            let identity = target project framework

            RpcValue.map
                [ "target", identity
                  "graphState", RpcValue.string "current"
                  "package",
                  RpcValue.map
                      [ "package", RpcValue.string "Example.Package"
                        "target", identity
                        "state",
                        RpcValue.map
                            [ "kind", RpcValue.string "direct"
                              "resolved", RpcValue.string "1.0.0" ] ] ]

        let value =
            RpcValue.map
                [ "requestId", RpcValue.string (fixtureRequestId.ToString "D")
                  "restore", RpcValue.string "refreshed"
                  "items",
                  RpcValue.array
                      [ item "/workspace/App.fsproj" "net10.0"
                        item "/workspace/Lib.fsproj" "net9.0" ] ]

        let snapshot, _, _ =
            PackageMapping.decodeInstalled fixtureRequestId value
            |> Result.defaultWith failwith

        snapshot.Items.Length |> should equal 2

        snapshot.Items
        |> List.map _.Target
        |> should
            equal
            [ { Project = ProjectId "/workspace/App.fsproj"
                Framework = Some(TargetFramework "net10.0")
                Runtime = None }
              { Project = ProjectId "/workspace/Lib.fsproj"
                Framework = Some(TargetFramework "net9.0")
                Runtime = None } ]

    [<Fact>]
    member _.``generated client requests use closed protocol methods and canonical request ids``() =
        let request =
            { Token = RequestToken 7L
              Target =
                SingleProject
                    { Id = ProjectId "/workspace/App.fsproj"
                      Name = "App"
                      Frameworks = [ TargetFramework "net10.0" ] }
              Source = Some(PackageSource "nuget.org")
              Query =
                { Text = "example"
                  IncludePrerelease = false
                  Page = 0
                  PageSize = 25 } }

        let bytes =
            RpcFrame.Request(
                2u,
                "package/search/start",
                PackageMapping.searchParameters Protocol.MaximumPageSize request None
            )
            |> MessagePackCodec.encode

        match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte> bytes) with
        | Ok(RpcFrame.Request(2u, "package/search/start", RpcValue.Map fields), consumed) ->
            consumed |> should equal bytes.Length

            fields
            |> RpcValue.requiredText "requestId"
            |> Result.defaultWith failwith
            |> Guid.Parse
            |> should equal (PackageMapping.requestIdentity request.Token)

            fields.Keys
            |> Set.ofSeq
            |> should equal (set [ "requestId"; "term"; "includePrerelease"; "source"; "pageSize" ])
        | actual -> failwith $"Expected the closed search request, got {actual}."
