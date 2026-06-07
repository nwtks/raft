module Raft.App

open Raft

let peers =
    [ { Id = 0
        Host = "127.0.0.1"
        Port = 5000 }
      { Id = 1
        Host = "127.0.0.1"
        Port = 5001 }
      { Id = 2
        Host = "127.0.0.1"
        Port = 5002 } ]

let createConfig nodeId =
    let myPort = (peers |> List.find (fun p -> p.Id = nodeId)).Port
    let otherPeers = peers |> List.filter (fun p -> p.Id <> nodeId)

    { NodeId = nodeId
      Host = "127.0.0.1"
      Port = myPort
      Peers = otherPeers
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500
      SnapshotAutoThreshold = 100 }

let printHelp () =
    printfn "Raft KVS Demo"
    printfn "Usage: dotnet run -- --node <id>"
    printfn "Valid node IDs: 0, 1, 2"

let printCommands () =
    printfn ""
    printfn "Commands:"
    printfn "  put <key> <value>   (Submit command to cluster)"
    printfn "  get <key>           (Read from local state machine)"
    printfn "  state               (Print current Raft state)"
    printfn "  quit                (Exit)"
    printfn ""

let printState (node: RaftNode) =
    let st = node.GetState()
    printfn "Role: %A, Term: %d, Leader: %A" st.Role st.Persistent.CurrentTerm st.CurrentLeader
    printfn "CommitIndex: %d, LastApplied: %d" st.Volatile.CommitIndex st.Volatile.LastApplied
    printfn "Log entries: %d" st.Persistent.Log.Count

let getValue (cmd: string) (node: RaftNode) kvs kvsLock =
    let p = cmd.Split ' '

    if p.Length = 2 then
        match node.LinearizableRead() with
        | ReadReady ->
            let v = lock kvsLock (fun () -> kvs |> Map.tryFind p.[1])

            match v with
            | Some v -> printfn "%s" v
            | None -> printfn "(not found)"
        | ReadRedirect None -> printfn "Error: Not the leader, and current leader is unknown."
        | ReadRedirect(Some leader) -> printfn "Redirect: leader is Node %d at %s:%d" leader.Id leader.Host leader.Port

let submitCommand cmd (node: RaftNode) =
    match node.SubmitCommand cmd with
    | Accepted -> ()
    | Redirect None -> printfn "Error: Not the leader, and current leader is unknown."
    | Redirect(Some leader) -> printfn "Redirect: leader is Node %d at %s:%d" leader.Id leader.Host leader.Port

[<TailCall>]
let rec inputLoop node kvs kvsLock =
    printf "> "
    let input = System.Console.ReadLine()

    if isNull input then
        ()
    else
        match input.Trim() with
        | "quit"
        | "q" -> ()
        | "" -> inputLoop node kvs kvsLock
        | "state" ->
            printState node
            inputLoop node kvs kvsLock
        | cmd when cmd.StartsWith "get " ->
            getValue cmd node kvs kvsLock
            inputLoop node kvs kvsLock
        | cmd when cmd.StartsWith "put " ->
            submitCommand cmd node
            inputLoop node kvs kvsLock
        | _ ->
            printfn "Unknown command."
            inputLoop node kvs kvsLock

let runNode nodeId =
    let kvsLock = obj ()
    let mutable kvs = Map.empty<string, string>

    let onApply entry =
        printfn "\n>>> Applied [%d:T%d]: %s" entry.Index entry.Term entry.Command

        match entry.Command.Split ' ' with
        | [| "put"; k; v |] -> lock kvsLock (fun () -> kvs <- kvs |> Map.add k v)
        | _ -> ()

        printf "> "

    let onInstallSnapshot (data: string) =
        lock kvsLock (fun () -> kvs <- System.Text.Json.JsonSerializer.Deserialize<Map<string, string>> data)
        printfn "\n>>> Snapshot installed with %d entries" (lock kvsLock (fun () -> kvs.Count))
        printf "> "

    let onGetSnapshotData () =
        lock kvsLock (fun () -> System.Text.Json.JsonSerializer.Serialize kvs)

    let config = createConfig nodeId
    printfn "Starting Node %d on port %d..." nodeId config.Port
    let transport = TcpTransport()
    let persistence = FilePersistence nodeId

    use node =
        new RaftNode(config, transport, persistence, onApply, onInstallSnapshot, onGetSnapshotData)

    System.Threading.Thread.Sleep 2000
    printCommands ()
    inputLoop node kvs kvsLock

let parseArgv argv =
    argv
    |> Array.toList
    |> function
        | "--node" :: idStr :: _ ->
            match System.Int32.TryParse idStr with
            | true, id when id >= 0 && id <= 2 -> Some id
            | _ -> None
        | _ -> None

[<EntryPoint>]
let main argv =
    match parseArgv argv with
    | None ->
        printHelp ()
        1
    | Some nodeId ->
        runNode nodeId
        0
