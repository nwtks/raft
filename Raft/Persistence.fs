namespace Raft

module Persistence =
    let log msg = printfn "[Persistence] %s" msg

    let save fileName state =
        let json = System.Text.Json.JsonSerializer.Serialize(state, JsonConfig.options)
        let tempFile = fileName + ".tmp"
        System.IO.File.WriteAllText(tempFile, json)
        System.IO.File.Move(tempFile, fileName, true)

    let load fileName =
        if System.IO.File.Exists fileName then
            try
                let json = System.IO.File.ReadAllText fileName
                Some(System.Text.Json.JsonSerializer.Deserialize<PersistentState>(json, JsonConfig.options))
            with ex ->
                log $"Failed to deserialize state file '{fileName}': {ex.Message}"
                None
        else
            None

type FilePersistence(nodeId: NodeId) =
    let fileName = sprintf "state_%d.json" nodeId

    interface IPersistence with
        member _.Save state = Persistence.save fileName state
        member _.Load() = Persistence.load fileName
