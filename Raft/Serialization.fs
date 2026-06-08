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

    override _.CanConvert typeToConvert =
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() = typedefof<option<_>>

    override _.CreateConverter(typeToConvert, options) =
        let innerType = typeToConvert.GetGenericArguments().[0]
        let converterType = typedefof<OptionConverter<_>>.MakeGenericType innerType
        System.Activator.CreateInstance converterType :?> System.Text.Json.Serialization.JsonConverter

type RaftMessageConverter() =
    inherit System.Text.Json.Serialization.JsonConverter<RaftMessage>()

    static let caseMap =
        Reflection.FSharpType.GetUnionCases typeof<RaftMessage>
        |> Array.map (fun ci -> ci.Name, ci)
        |> Map.ofArray

    override _.Read(reader, typeToConvert, options) =
        use doc = System.Text.Json.JsonDocument.ParseValue(&reader)
        let root = doc.RootElement
        let caseName = root.GetProperty("Case").GetString()
        let fields = root.GetProperty "Fields"

        match caseMap |> Map.tryFind caseName with
        | Some caseInfo ->
            let fieldType = caseInfo.GetFields().[0].PropertyType

            let payload =
                System.Text.Json.JsonSerializer.Deserialize(fields.[0].GetRawText(), fieldType, options)

            Reflection.FSharpValue.MakeUnion(caseInfo, [| payload |]) :?> RaftMessage
        | None -> failwithf "Unknown message case: %s" caseName

    override _.Write(writer, value, options) =
        writer.WriteStartObject()

        let caseInfo, fields =
            Reflection.FSharpValue.GetUnionFields(value, typeof<RaftMessage>)

        writer.WriteString("Case", caseInfo.Name)
        writer.WriteStartArray "Fields"
        System.Text.Json.JsonSerializer.Serialize(writer, fields.[0], options)
        writer.WriteEndArray()
        writer.WriteEndObject()

module JsonConfig =
    let options =
        let opts = System.Text.Json.JsonSerializerOptions()
        opts.Converters.Add(OptionConverterFactory())
        opts.Converters.Add(RaftMessageConverter())
        opts
