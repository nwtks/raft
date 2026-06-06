namespace Raft

type OptionConverter<'T>() =
    inherit System.Text.Json.Serialization.JsonConverter<'T option>()

    override _.Read(reader, typeToConvert, options) =
        if reader.TokenType = System.Text.Json.JsonTokenType.Null then
            None
        else
            let value = System.Text.Json.JsonSerializer.Deserialize<'T>(&reader, options)
            Some value

    override _.Write(writer, value, options) =
        match value with
        | None -> writer.WriteNullValue()
        | Some v -> System.Text.Json.JsonSerializer.Serialize(writer, v, options)

type OptionConverterFactory() =
    inherit System.Text.Json.Serialization.JsonConverterFactory()

    override _.CanConvert(typeToConvert) =
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() = typedefof<option<_>>

    override _.CreateConverter(typeToConvert, options) =
        let innerType = typeToConvert.GetGenericArguments().[0]
        let converterType = typedefof<OptionConverter<_>>.MakeGenericType innerType
        System.Activator.CreateInstance converterType :?> System.Text.Json.Serialization.JsonConverter

type RaftMessageConverter() =
    inherit System.Text.Json.Serialization.JsonConverter<RaftMessage>()

    override _.Read(reader, typeToConvert, options) =
        use doc = System.Text.Json.JsonDocument.ParseValue(&reader)
        let root = doc.RootElement
        let caseName = root.GetProperty("Case").GetString()
        let fields = root.GetProperty "Fields"

        match caseName with
        | "RequestVoteMsg" ->
            let payload =
                System.Text.Json.JsonSerializer.Deserialize<RequestVote>(fields.[0].GetRawText(), options)

            RequestVoteMsg payload
        | "RequestVoteResponseMsg" ->
            let payload =
                System.Text.Json.JsonSerializer.Deserialize<RequestVoteResponse>(fields.[0].GetRawText(), options)

            RequestVoteResponseMsg payload
        | "AppendEntriesMsg" ->
            let payload =
                System.Text.Json.JsonSerializer.Deserialize<AppendEntries>(fields.[0].GetRawText(), options)

            AppendEntriesMsg payload
        | "AppendEntriesResponseMsg" ->
            let payload =
                System.Text.Json.JsonSerializer.Deserialize<AppendEntriesResponse>(fields.[0].GetRawText(), options)

            AppendEntriesResponseMsg payload
        | "InstallSnapshotMsg" ->
            let payload =
                System.Text.Json.JsonSerializer.Deserialize<InstallSnapshot>(fields.[0].GetRawText(), options)

            InstallSnapshotMsg payload
        | "InstallSnapshotResponseMsg" ->
            let payload =
                System.Text.Json.JsonSerializer.Deserialize<InstallSnapshotResponse>(fields.[0].GetRawText(), options)

            InstallSnapshotResponseMsg payload
        | _ -> failwithf "Unknown message case: %s" caseName

    override _.Write(writer, value, options) =
        writer.WriteStartObject()

        match value with
        | RequestVoteMsg rv ->
            writer.WriteString("Case", "RequestVoteMsg")
            writer.WriteStartArray "Fields"
            System.Text.Json.JsonSerializer.Serialize(writer, rv, options)
            writer.WriteEndArray()
        | RequestVoteResponseMsg rvr ->
            writer.WriteString("Case", "RequestVoteResponseMsg")
            writer.WriteStartArray "Fields"
            System.Text.Json.JsonSerializer.Serialize(writer, rvr, options)
            writer.WriteEndArray()
        | AppendEntriesMsg ae ->
            writer.WriteString("Case", "AppendEntriesMsg")
            writer.WriteStartArray "Fields"
            System.Text.Json.JsonSerializer.Serialize(writer, ae, options)
            writer.WriteEndArray()
        | AppendEntriesResponseMsg aer ->
            writer.WriteString("Case", "AppendEntriesResponseMsg")
            writer.WriteStartArray "Fields"
            System.Text.Json.JsonSerializer.Serialize(writer, aer, options)
            writer.WriteEndArray()
        | InstallSnapshotMsg snap ->
            writer.WriteString("Case", "InstallSnapshotMsg")
            writer.WriteStartArray "Fields"
            System.Text.Json.JsonSerializer.Serialize(writer, snap, options)
            writer.WriteEndArray()
        | InstallSnapshotResponseMsg snapResp ->
            writer.WriteString("Case", "InstallSnapshotResponseMsg")
            writer.WriteStartArray "Fields"
            System.Text.Json.JsonSerializer.Serialize(writer, snapResp, options)
            writer.WriteEndArray()
        | AddPeer _ -> failwith "AddPeer cannot be serialized."
        | RemovePeer _ -> failwith "RemovePeer cannot be serialized."
        | ClientCommand _ -> failwith "ClientCommand cannot be serialized."

        writer.WriteEndObject()
