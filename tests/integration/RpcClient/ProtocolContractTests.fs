namespace Dotnet.PackageExplorer.RpcClient.IntegrationTests

open System
open System.IO
open Dotnet.PackageExplorer.Application
open Dotnet.PackageExplorer.RpcClient
open FsUnit.Xunit
open Xunit

[<Sealed>]
type ProtocolContractTests() =
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
