namespace Raft

open System.IO
open System.Text.Json
open System.Text.Json.Serialization

module Persistence =
    let options = JsonSerializerOptions()
    do options.Converters.Add(JsonFSharpConverter())

    let save fileName (state: PersistentState) =
        let json = JsonSerializer.Serialize(state, options)
        let tempFile = fileName + ".tmp"
        File.WriteAllText(tempFile, json)
        File.Move(tempFile, fileName, true)

    let load fileName =
        if File.Exists fileName then
            try
                let json = File.ReadAllText fileName
                Some(JsonSerializer.Deserialize<PersistentState>(json, options))
            with _ ->
                None
        else
            None

type FilePersistence(nodeId: NodeId) =
    let fileName = sprintf "state_%d.json" nodeId

    interface IPersistence with
        member _.Save(state: PersistentState) = Persistence.save fileName state
        member _.Load() = Persistence.load fileName
