namespace Raft

module Persistence =
    let log msg = printfn "[Persistence] %s" msg

    let save fileName state =
        let json = System.Text.Json.JsonSerializer.Serialize(state, JsonConfig.options)
        let tempFile = fileName + ".tmp"

        use fs =
            new System.IO.FileStream(
                tempFile,
                System.IO.FileMode.Create,
                System.IO.FileAccess.Write,
                System.IO.FileShare.None,
                bufferSize = 4096,
                options = System.IO.FileOptions.SequentialScan
            )

        let bytes = System.Text.Encoding.UTF8.GetBytes json
        fs.Write(bytes, 0, bytes.Length)
        fs.Flush true
        System.IO.File.Move(tempFile, fileName, true)

    let load fileName =
        let tempFile = fileName + ".tmp"

        if System.IO.File.Exists tempFile then
            System.IO.File.Delete tempFile

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
