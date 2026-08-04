namespace Dotnet.PackageExplorer.RpcClient

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.Text
open MessagePack

[<AutoOpen>]
module internal ResultSyntax =
    type ResultBuilder() =
        member _.Bind(value, continuation) = Result.bind continuation value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()

        member _.Combine(value, continuation) =
            Result.bind (fun () -> continuation) value

        member _.Delay(continuation) = continuation ()

    let result = ResultBuilder()

[<RequireQualifiedAccess>]
type internal RpcValue =
    | Nil
    | Boolean of bool
    | Integer of int64
    | Float of float
    | String of string
    | Binary of byte array
    | Array of RpcValue list
    | Map of Map<string, RpcValue>

type internal RpcError =
    { Code: string
      Message: string
      Data: RpcValue option }

[<RequireQualifiedAccess>]
type internal RpcFrame =
    | Request of id: uint32 * methodName: string * parameters: RpcValue
    | Response of id: uint32 * result: Result<RpcValue, RpcError>
    | Notification of methodName: string * parameters: RpcValue

[<RequireQualifiedAccess>]
type internal DecodeFailure =
    | Incomplete
    | Invalid of string
    | TooLarge

[<RequireQualifiedAccess>]
module internal RpcValue =
    let map values = values |> Map.ofList |> RpcValue.Map
    let array values = values |> List.ofSeq |> RpcValue.Array
    let string value = RpcValue.String value
    let integer value = RpcValue.Integer(int64 value)

    let fields name value =
        match value with
        | RpcValue.Map fields -> Ok fields
        | _ -> Error $"{name} must be a map."

    let arrayItems name value =
        match value with
        | RpcValue.Array values -> Ok values
        | _ -> Error $"{name} must be an array."

    let field name fields =
        fields
        |> Map.tryFind name
        |> function
            | Some value -> Ok value
            | None -> Error $"Missing field '{name}'."

    let optional name fields = Map.tryFind name fields

    let text name value =
        match value with
        | RpcValue.String text -> Ok text
        | _ -> Error $"{name} must be a string."

    let number name value =
        match value with
        | RpcValue.Integer number -> Ok number
        | _ -> Error $"{name} must be an integer."

    let boolean name value =
        match value with
        | RpcValue.Boolean boolean -> Ok boolean
        | _ -> Error $"{name} must be a boolean."

    let requiredText name fields =
        field name fields |> Result.bind (text name)

    let optionalText name fields =
        match optional name fields with
        | None
        | Some RpcValue.Nil -> Ok None
        | Some value -> text name value |> Result.map Some

    let requiredArray name fields =
        field name fields |> Result.bind (arrayItems name)

[<RequireQualifiedAccess>]
module internal Protocol =
    [<Literal>]
    let MaximumFrameBytes = 16 * 1024 * 1024

    [<Literal>]
    let MaximumDepth = 64

    [<Literal>]
    let MaximumPageSize = 200

    [<Literal>]
    let NegotiatedFrameBytes = 1024 * 1024

    [<Literal>]
    let MaximumArrayItems = 1000000

    [<Literal>]
    let MaximumMapItems = 500000

    let capabilities =
        [ "packages.sources.v1"
          "packages.source-mapping.v1"
          "packages.search.v1"
          "packages.details.v1"
          "packages.readme.v1"
          "packages.installed.v1"
          "packages.restore.v1"
          "packages.updates.v1"
          "packages.consolidation.v1"
          "packages.preview.v1"
          "packages.batch-preview.v1"
          "packages.execute.v1"
          "packages.batch-execute.v1"
          "packages.cancel.v1"
          "packages.partial-recovery.v1" ]

[<RequireQualifiedAccess>]
module internal MessagePackCodec =
    let private strictUtf8 = UTF8Encoding(false, true)

    let private security maximumFrameBytes =
        MessagePackSecurity.UntrustedData
            .WithMaximumObjectGraphDepth(Protocol.MaximumDepth)
            .WithMaximumDecompressedSize
            maximumFrameBytes

    let private safeMessage (error: exn) =
        match error with
        | :? DecoderFallbackException -> "MessagePack strings must contain valid UTF-8."
        | :? InsufficientExecutionStackException ->
            "MessagePack nesting exceeds the configured limit."
        | :? OverflowException -> "MessagePack numeric value is outside the supported range."
        | :? MessagePackSerializationException -> "The MessagePack value is malformed."
        | _ -> "The MessagePack value is malformed."

    let private readString (reader: byref<MessagePackReader>) =
        let bytes = reader.ReadStringSequence()

        if not bytes.HasValue then
            invalidArg "value" "Expected a string."

        let sequence = bytes.Value
        let buffer = Array.zeroCreate<byte> (int sequence.Length)
        sequence.CopyTo buffer
        strictUtf8.GetString buffer

    let rec private readValue
        maximumFrameBytes
        (configuredSecurity: MessagePackSecurity)
        (reader: byref<MessagePackReader>)
        =
        configuredSecurity.DepthStep &reader

        try
            match reader.NextMessagePackType with
            | MessagePackType.Nil ->
                reader.ReadNil() |> ignore
                RpcValue.Nil
            | MessagePackType.Boolean -> RpcValue.Boolean(reader.ReadBoolean())
            | MessagePackType.Integer ->
                if reader.NextCode >= 0xccuy && reader.NextCode <= 0xcfuy then
                    let value = reader.ReadUInt64()

                    if value > uint64 Int64.MaxValue then
                        raise (OverflowException())

                    RpcValue.Integer(int64 value)
                else
                    RpcValue.Integer(reader.ReadInt64())
            | MessagePackType.Float -> RpcValue.Float(reader.ReadDouble())
            | MessagePackType.String -> RpcValue.String(readString &reader)
            | MessagePackType.Binary ->
                let bytes = reader.ReadBytes()

                if not bytes.HasValue then
                    invalidArg "value" "Expected binary data."

                let value = Array.zeroCreate<byte> (int bytes.Value.Length)
                bytes.Value.CopyTo value
                RpcValue.Binary value
            | MessagePackType.Array ->
                let count = reader.ReadArrayHeader()

                if count > min Protocol.MaximumArrayItems maximumFrameBytes then
                    invalidArg "value" "The MessagePack array is too large."

                let values = ResizeArray<RpcValue>()

                for _ in 1..count do
                    values.Add(readValue maximumFrameBytes configuredSecurity &reader)

                values |> Seq.toList |> RpcValue.Array
            | MessagePackType.Map ->
                let count = reader.ReadMapHeader()

                if count > min Protocol.MaximumMapItems (maximumFrameBytes / 2) then
                    invalidArg "value" "The MessagePack map is too large."

                let mutable values = Map.empty

                for _ in 1..count do
                    if reader.NextMessagePackType <> MessagePackType.String then
                        invalidArg "value" "MessagePack map keys must be strings."

                    let key = readString &reader

                    if String.IsNullOrEmpty key || values.ContainsKey key then
                        invalidArg "value" "MessagePack map keys must be unique and non-empty."

                    values <-
                        values.Add(key, readValue maximumFrameBytes configuredSecurity &reader)

                RpcValue.Map values
            | MessagePackType.Extension ->
                invalidArg "value" "MessagePack extension values are not supported."
            | _ -> invalidArg "value" "Unsupported MessagePack value."
        finally
            reader.Depth <- reader.Depth - 1

    let private readId value =
        match value with
        | RpcValue.Integer value when value >= 0L && value <= int64 UInt32.MaxValue ->
            Ok(uint32 value)
        | _ -> Error(DecodeFailure.Invalid "The RPC message id is invalid.")

    let private readError value =
        match value with
        | RpcValue.Nil -> Ok None
        | RpcValue.Map fields ->
            match RpcValue.requiredText "code" fields, RpcValue.requiredText "message" fields with
            | Ok code, Ok message ->
                Ok(
                    Some
                        { Code = code
                          Message = message
                          Data = Map.tryFind "data" fields }
                )
            | _ -> Error(DecodeFailure.Invalid "The RPC error is invalid.")
        | _ -> Error(DecodeFailure.Invalid "The RPC error is invalid.")

    let private frame value =
        match value with
        | RpcValue.Array [ RpcValue.Integer 1L; id; error; result ] ->
            match readId id, readError error with
            | Ok messageId, Ok None -> Ok(RpcFrame.Response(messageId, Ok result))
            | Ok messageId, Ok(Some failure) -> Ok(RpcFrame.Response(messageId, Error failure))
            | Error failure, _
            | _, Error failure -> Error failure
        | RpcValue.Array [ RpcValue.Integer 2L; RpcValue.String methodName; parameters ] when
            not (String.IsNullOrWhiteSpace methodName)
            ->
            match parameters with
            | RpcValue.Map _ -> Ok(RpcFrame.Notification(methodName, parameters))
            | _ -> Error(DecodeFailure.Invalid "The notification parameters are invalid.")
        | RpcValue.Array [ RpcValue.Integer 0L; id; RpcValue.String methodName; parameters ] ->
            match readId id, parameters with
            | Ok messageId, RpcValue.Map _ ->
                Ok(RpcFrame.Request(messageId, methodName, parameters))
            | Error failure, _ -> Error failure
            | _ -> Error(DecodeFailure.Invalid "The request parameters are invalid.")
        | _ -> Error(DecodeFailure.Invalid "The MessagePack-RPC frame is invalid.")

    let tryReadFrameWithLimit maximumFrameBytes (bytes: ReadOnlyMemory<byte>) =
        if maximumFrameBytes < 1 || maximumFrameBytes > Protocol.MaximumFrameBytes then
            invalidArg "maximumFrameBytes" "The frame limit is outside the secure profile."

        try
            let mutable reader = MessagePackReader bytes
            let configuredSecurity = security maximumFrameBytes
            let value = readValue maximumFrameBytes configuredSecurity &reader
            let consumed = int reader.Consumed

            if consumed > maximumFrameBytes then
                Error DecodeFailure.TooLarge
            else
                value |> frame |> Result.map (fun decoded -> decoded, consumed)
        with
        | :? EndOfStreamException ->
            if bytes.Length > maximumFrameBytes then
                Error DecodeFailure.TooLarge
            else
                Error DecodeFailure.Incomplete
        | error -> Error(DecodeFailure.Invalid(safeMessage error))

    let tryReadFrame bytes =
        tryReadFrameWithLimit Protocol.MaximumFrameBytes bytes

    let rec private writeValue (writer: byref<MessagePackWriter>) value =
        match value with
        | RpcValue.Nil -> writer.WriteNil()
        | RpcValue.Boolean value -> writer.Write value
        | RpcValue.Integer value -> writer.Write value
        | RpcValue.Float value -> writer.Write value
        | RpcValue.String value -> writer.Write value
        | RpcValue.Binary value -> writer.Write value
        | RpcValue.Array values ->
            writer.WriteArrayHeader values.Length

            for value in values do
                writeValue &writer value
        | RpcValue.Map values ->
            writer.WriteMapHeader values.Count

            for KeyValue(key, value) in values do
                writer.Write key
                writeValue &writer value

    let encode frame =
        let value =
            match frame with
            | RpcFrame.Request(id, methodName, parameters) ->
                RpcValue.Array
                    [ RpcValue.Integer 0L
                      RpcValue.Integer(int64 id)
                      RpcValue.String methodName
                      parameters ]
            | RpcFrame.Response(id, outcome) ->
                let error, result =
                    match outcome with
                    | Ok result -> RpcValue.Nil, result
                    | Error error ->
                        RpcValue.map
                            [ "code", RpcValue.String error.Code
                              "message", RpcValue.String error.Message
                              "data", error.Data |> Option.defaultValue RpcValue.Nil ],
                        RpcValue.Nil

                RpcValue.Array [ RpcValue.Integer 1L; RpcValue.Integer(int64 id); error; result ]
            | RpcFrame.Notification(methodName, parameters) ->
                RpcValue.Array [ RpcValue.Integer 2L; RpcValue.String methodName; parameters ]

        let buffer = ArrayBufferWriter<byte>()
        let mutable writer = MessagePackWriter buffer
        writeValue &writer value
        writer.Flush()
        buffer.WrittenMemory.ToArray()
