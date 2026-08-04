namespace Dotnet.PackageExplorer.RpcClient.UnitTests

open System
open System.IO
open System.Text.Json
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open FsUnit.Xunit
open Xunit

[<Sealed>]
type ProtocolFixtureTests() =
    let fixtureRequestId = Guid.Parse "11111111-1111-1111-1111-111111111111"

    let fixture name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "PackageRpc", name)
        |> File.ReadAllBytes

    let frame name =
        let bytes = fixture name

        match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte> bytes) with
        | Ok(frame, consumed) when consumed = bytes.Length -> frame
        | Ok _ -> failwith $"The {name} fixture was not consumed exactly."
        | Error failure -> failwith $"The {name} fixture failed to decode: {failure}."

    let response name =
        match frame name with
        | RpcFrame.Response(_, Ok value) -> value
        | actual -> failwith $"The {name} fixture was not a successful response: {actual}."

    let notificationResult name =
        match frame name with
        | RpcFrame.Notification(_, RpcValue.Map fields) ->
            fields
            |> Map.tryFind "result"
            |> Option.defaultWith (fun () -> failwith $"The {name} fixture has no result.")
        | actual -> failwith $"The {name} fixture was not a notification: {actual}."

    [<Fact>]
    member _.``all core-owned package protocol golden frames obey the client framing rules``() =
        let directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PackageRpc")
        let fixtures = Directory.GetFiles(directory, "*.mpack") |> Array.sort

        fixtures.Length |> should equal 47

        fixtures
        |> Array.iter (fun path ->
            let bytes = File.ReadAllBytes path

            match MessagePackCodec.tryReadFrame (ReadOnlyMemory<byte> bytes) with
            | Ok(_, consumed) -> consumed |> should equal bytes.Length
            | Error failure -> failwith $"{Path.GetFileName path} failed to decode: {failure}.")

    [<Fact>]
    member _.``the client consumes the core schema limits instead of widening the wire contract``
        ()
        =
        let path =
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "PackageRpc",
                "package-v1.schema.json"
            )

        use document = JsonDocument.Parse(File.ReadAllText path)
        let root = document.RootElement
        let version = root.GetProperty("version")
        let limits = root.GetProperty("limits")

        version.GetProperty("major").GetInt32() |> should equal 1
        version.GetProperty("minor").GetInt32() |> should equal 0

        limits.GetProperty("maximumFrameBytes").GetInt32()
        |> should equal Protocol.MaximumFrameBytes

        limits.GetProperty("maximumDepth").GetInt32()
        |> should equal Protocol.MaximumDepth

        limits.GetProperty("maximumPageSize").GetInt32()
        |> should equal Protocol.MaximumPageSize

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

        let complete = fixture "preview-response.mpack"
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
            let excessive = Array.zeroCreate<byte> (5 + (count * valuesPerItem))
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
    member _.``golden package results retain source package target preview and recovery identities``
        ()
        =
        let sources =
            response "sources-response.mpack"
            |> PackageMapping.decodeSources
            |> Result.defaultWith failwith

        sources
        |> should
            equal
            [ { Id = PackageSource "nuget.org"
                Name = "nuget.org"
                Location = "https://api.nuget.org/v3/index.json"
                Availability = Available } ]

        let query =
            { Text = "example"
              IncludePrerelease = false
              Page = 0
              PageSize = 25 }

        let search, references, continuation =
            notificationResult "search-completed.mpack"
            |> PackageMapping.decodeSearch fixtureRequestId query
            |> Result.defaultWith failwith

        search.Query |> should equal query
        search.Packages |> should be Empty
        references |> should be Empty
        continuation |> should equal None

        let details, readme =
            response "details-response.mpack"
            |> PackageMapping.decodeDetails
            |> Result.defaultWith failwith

        details.Package.Id |> should equal (PackageId "Example.Package")
        details.IsDeprecated |> should equal true
        readme |> should equal (Some "# Example")

        let installed, restore, continuation =
            response "installed-response.mpack"
            |> PackageMapping.decodeInstalled fixtureRequestId
            |> Result.defaultWith failwith

        installed.Items.Head.Package.Id |> should equal (PackageId "Example.Package")
        installed.Items.Head.Package.Kind |> should equal (Some Direct)

        installed.Items.Head.Target.Project
        |> should equal (ProjectId "/workspace/App.fsproj")

        restore |> should equal "inProgress"
        continuation |> should equal (Some "1")

        let operation = UpdateSelectedPackages(Set.singleton (PackageId "Example.Package"))

        let preview =
            response "preview-response.mpack"
            |> PackageMapping.decodePreview operation false
            |> Result.defaultWith failwith

        preview.Id |> should equal (PreviewId "PREVIEW-TOKEN")

        preview.Projects.Head.Project
        |> should equal (ProjectId "/workspace/App.fsproj")

        preview.Dependencies.Head.Id |> should equal (PackageId "Dependency")

        let recovery =
            match frame "operation-error.mpack" with
            | RpcFrame.Notification(_, RpcValue.Map fields) ->
                match Map.tryFind "error" fields with
                | Some(RpcValue.Map error) ->
                    PackageMapping.decodeRecovery (Map.tryFind "data" error)
                    |> Result.defaultWith failwith
                | _ -> failwith "Expected a typed operation error."
            | _ -> failwith "Expected an operation completion notification."

        recovery |> should be Empty

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
    member _.``golden discovery and operation frames retain paging and progress identities``() =
        let mapping =
            response "source-mapping-response.mpack"
            |> PackageMapping.decodeSourceMapping
            |> Result.defaultWith failwith

        mapping.Kind |> should equal ApplyAllowed
        mapping.Sources |> should equal [ PackageSource "nuget.org" ]

        let updates =
            notificationResult "updates-completed.mpack"
            |> PackageMapping.decodeUpdates
            |> Result.defaultWith failwith

        updates.Updates.Head.Package |> should equal (PackageId "Example.Package")

        updates.Updates.Head.Target.Project
        |> should equal (ProjectId "/workspace/App.fsproj")

        updates.Continuation |> should equal (Some "1")

        let consolidation =
            notificationResult "consolidation-completed.mpack"
            |> PackageMapping.decodeConsolidation
            |> Result.defaultWith failwith

        consolidation.Packages.Head.Package
        |> should equal (PackageId "Example.Package")

        consolidation.Packages.Head.CandidateVersions
        |> should equal [ PackageVersion "2.0.0" ]

        let batchOperation =
            UpdateSelectedPackages(Set.singleton (PackageId "Example.Package"))

        let batchPreview =
            response "preview-batch-response.mpack"
            |> PackageMapping.decodePreview batchOperation true
            |> Result.defaultWith failwith

        batchPreview.Id |> should equal (PreviewId "BATCH-PREVIEW-TOKEN")
        batchPreview.Operation |> should equal batchOperation

        let progress =
            match frame "operation-progress.mpack" with
            | RpcFrame.Notification(_, RpcValue.Map fields) ->
                fields
                |> Map.find "progress"
                |> PackageMapping.decodeProgress
                    (RequestToken 14L)
                    (PreviewId "BATCH-PREVIEW-TOKEN")
                |> Result.defaultWith failwith
            | actual -> failwith $"Expected operation progress, got {actual}."

        fst progress |> should equal (RequestToken 14L)

        (snd progress).Preview |> should equal (PreviewId "BATCH-PREVIEW-TOKEN")

        (snd progress).Operation
        |> should equal (OperationId "22222222-2222-2222-2222-222222222222")

        (snd progress).Completed |> should equal 1
        (snd progress).Total |> should equal 2

        let operation =
            notificationResult "operation-completed.mpack"
            |> PackageMapping.decodeExecution
            |> Result.defaultWith failwith

        fst operation |> should equal "22222222-2222-2222-2222-222222222222"

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
