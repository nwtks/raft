module Raft.App

open System.Threading
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
      HeartbeatIntervalMs = 500 }

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
    printfn "Log length: %d" (List.length st.Persistent.Log)

let getValue (cmd: string) kvs =
    let p = cmd.Split(' ')

    if p.Length = 2 then
        match kvs |> Map.tryFind p.[1] with
        | Some v -> printfn "%s" v
        | None -> printfn "(not found)"

let submitCommand (cmd: string) (node: RaftNode) =
    let success = node.SubmitCommand cmd

    if not success then
        printfn "Error: Not the leader. Cannot accept writes."

[<TailCall>]
let rec inputLoop (node: RaftNode) kvs =
    printf "> "
    let input = System.Console.ReadLine()

    if isNull input then
        ()
    else
        match input.Trim() with
        | "quit"
        | "q" -> ()
        | "" -> inputLoop node kvs
        | "state" ->
            printState node
            inputLoop node kvs
        | cmd when cmd.StartsWith "get " ->
            getValue cmd kvs
            inputLoop node kvs
        | cmd when cmd.StartsWith "put " ->
            submitCommand cmd node
            inputLoop node kvs
        | _ ->
            printfn "Unknown command."
            inputLoop node kvs

let runNode nodeId =
    let mutable kvs = Map.empty<string, string>

    let onApply (entry: LogEntry) =
        printfn "\n>>> Applied [%d:T%d]: %s" entry.Index entry.Term entry.Command

        match entry.Command.Split(' ') with
        | [| "put"; k; v |] -> kvs <- kvs |> Map.add k v
        | _ -> ()

        printf "> "

    let config = createConfig nodeId
    printfn "Starting Node %d on port %d..." nodeId config.Port
    let transport = TcpTransport()
    let persistence = FilePersistence nodeId
    let node = RaftNode(config, transport, persistence, onApply)
    Thread.Sleep 2000
    printCommands ()
    inputLoop node kvs

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
