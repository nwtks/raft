namespace Raft.App

open Raft

module App =
    let createConfig nodeId port initialPeers =
        let otherPeers = initialPeers |> List.filter (fun p -> p.Id <> nodeId)

        { NodeId = nodeId
          Host = "127.0.0.1"
          Port = port
          Peers = otherPeers
          ElectionTimeoutMinMs = 1500
          ElectionTimeoutMaxMs = 3000
          HeartbeatIntervalMs = 500
          SnapshotAutoThreshold = 100 }

    let waitForPort port (timeoutMs: int) =
        System.Threading.SpinWait.SpinUntil(
            (fun () ->
                try
                    use client = new System.Net.Sockets.TcpClient()
                    client.Connect("127.0.0.1", port)
                    true
                with _ ->
                    false),
            timeoutMs
        )
        |> ignore

    let printHelp () =
        printfn "Raft KVS Demo"
        printfn "Usage: dotnet run -- --node <id> --port <port> [--peer <id> <host> <port>]..."
        printfn ""
        printfn "If --node <id> is already among the --peer list, the node starts as a regular member."
        printfn "If --node <id> is NOT in the --peer list, the node contacts existing peers to"
        printfn "  request being added as a non-voting member (catch-up)."
        printfn ""
        printfn "Examples:"
        printfn "  # Start a 3-node cluster:"
        printfn "  dotnet run -- --node 0 --port 5000 --peer 1 127.0.0.1 5001 --peer 2 127.0.0.1 5002"
        printfn "  dotnet run -- --node 1 --port 5001 --peer 0 127.0.0.1 5000 --peer 2 127.0.0.1 5002"
        printfn "  dotnet run -- --node 2 --port 5002 --peer 0 127.0.0.1 5000 --peer 1 127.0.0.1 5001"
        printfn ""
        printfn "  # Add a new node 3 (auto-requests join via existing peer):"

        printfn
            "  dotnet run -- --node 3 --port 5003 --peer 0 127.0.0.1 5000 --peer 1 127.0.0.1 5001 --peer 2 127.0.0.1 5002"

    let printCommands () =
        printfn ""
        printfn "Commands:"
        printfn "  put <key> <value>    (Submit command to cluster)"
        printfn "  get <key>            (Linearizable read from state machine)"
        printfn "  state                (Print current Raft state summary)"
        printfn "  dump                 (Print full Raft state details)"
        printfn "  log                  (Print log entries)"
        printfn "  cluster              (Show cluster membership)"
        printfn "  snapshot             (Take a manual snapshot)"
        printfn "  election             (Force election timeout)"
        printfn "  heartbeat            (Force heartbeat broadcast)"
        printfn "  add-peer <id> <h> <p> (Add a new peer to the cluster)"
        printfn "  remove-peer <id>     (Remove a peer from the cluster)"
        printfn "  quit                 (Exit)"
        printfn ""

    let printState (node: RaftNode) =
        let st = node.GetState()
        printfn $"Role: {st.Role}, Term: {st.Persistent.CurrentTerm}, Leader: {st.CurrentLeader}"
        printfn $"CommitIndex: {st.Volatile.CommitIndex}, LastApplied: {st.Volatile.LastApplied}"
        printfn $"Log entries: {st.Persistent.Log.Count}"

    let printDump (node: RaftNode) =
        let st = node.GetState()
        printfn "══════════════════════ Raft State Dump ══════════════════════"
        printfn $"  Role:              {st.Role}"
        printfn $"  CurrentTerm:       {st.Persistent.CurrentTerm}"
        printfn $"  VotedFor:          {st.Persistent.VotedFor}"
        printfn $"  CurrentLeader:     {st.CurrentLeader}"
        printfn $"  CommitIndex:       {st.Volatile.CommitIndex}"
        printfn $"  LastApplied:       {st.Volatile.LastApplied}"
        printfn $"  Log entries:       {st.Persistent.Log.Count}"

        match st.Persistent.Snapshot with
        | Some snap ->
            printfn
                $"  Snapshot:          LastIncludedIndex={snap.LastIncludedIndex}, LastIncludedTerm={snap.LastIncludedTerm}"
        | None -> printfn $"  Snapshot:          (none)"

        if st.Persistent.SessionTable |> Map.isEmpty |> not then
            printfn $"  SessionTable:"

            st.Persistent.SessionTable
            |> Map.iter (fun cId seqNum -> printfn $"    {cId}: seq={seqNum}")

        printfn $"  ConfigPhase:       {st.ConfigPhase}"
        printfn $"  LastConfigIndex:   {st.Persistent.LastConfigIndex}"
        printfn $"  Peers ({st.Config.Peers.Length}):"

        st.Config.Peers
        |> List.iter (fun p -> printfn $"    {p.Id} -> {p.Host}:{p.Port}")

        if st.NonVotingPeers |> List.isEmpty |> not then
            printfn $"  NonVotingPeers ({st.NonVotingPeers.Length}):"

            st.NonVotingPeers
            |> List.iter (fun p -> printfn $"    {p.Id} -> {p.Host}:{p.Port}")

        match st.LeaderState with
        | Some ls ->
            printfn $"  NextIndex:         {ls.NextIndex}"
            printfn $"  MatchIndex:        {ls.MatchIndex}"
        | None -> ()

        printfn "════════════════════════════════════════════════════════════"

    let printCluster (node: RaftNode) =
        let st = node.GetState()
        printfn $"ConfigPhase:       {st.ConfigPhase}"
        printfn $"CurrentLeader:     {st.CurrentLeader}"
        printfn $"Peers ({st.Config.Peers.Length}):"

        st.Config.Peers
        |> List.iter (fun p ->
            let isLeader =
                st.CurrentLeader
                |> Option.map (fun id -> id = p.Id)
                |> Option.defaultValue false

            let leaderMark = if isLeader then "*" else " "
            printfn $"  [{leaderMark}] {p.Id} -> {p.Host}:{p.Port}")

        if st.NonVotingPeers |> List.isEmpty |> not then
            printfn $"NonVotingPeers ({st.NonVotingPeers.Length}):"

            st.NonVotingPeers
            |> List.iter (fun p -> printfn $"  {p.Id} -> {p.Host}:{p.Port}")

    let printLog (node: RaftNode) =
        let st = node.GetState()

        match st.Persistent.Snapshot with
        | Some snap ->
            printfn $"Snapshot: LastIncludedIndex={snap.LastIncludedIndex}, LastIncludedTerm={snap.LastIncludedTerm}"
        | None -> ()

        if st.Persistent.Log |> Map.isEmpty then
            printfn "(empty log)"
        else
            printfn $"{st.Persistent.Log.Count} entries (committed up to index {st.Volatile.CommitIndex}):"
            printfn "  idx  term  clientId    seq  command"

            st.Persistent.Log
            |> Map.toSeq
            |> Seq.sortBy fst
            |> Seq.iter (fun (idx, entry) ->
                let committed = if idx <= st.Volatile.CommitIndex then "C" else " "
                let clientId = entry.ClientId |> Option.defaultValue ""
                let seqNum = entry.SeqNum |> Option.map string |> Option.defaultValue ""
                printfn $"  [{committed}] {idx, 3}  T{entry.Term, -3} {clientId, -10} {seqNum, -3}  {entry.Command}")

    let getValue (cmd: string) (node: RaftNode) kvs kvsLock =
        let p = cmd.Split ' '

        if p.Length = 2 then
            match node.LinearizableRead() with
            | ReadReady ->
                let v = lock kvsLock (fun () -> kvs |> Map.tryFind p.[1])

                match v with
                | Some v -> printfn $"{v}"
                | None -> printfn "(not found)"
            | ReadRedirect(Some leader) ->
                printfn $"Redirect: leader is Node {leader.Id} at {leader.Host}:{leader.Port}"
            | ReadRedirect None -> eprintfn "Error: Not the leader, and current leader is unknown."

    let submitCommand cmd (node: RaftNode) =
        match node.SubmitCommand cmd with
        | Accepted -> ()
        | Redirect(Some leader) -> printfn $"Redirect: leader is Node {leader.Id} at {leader.Host}:{leader.Port}"
        | Redirect None -> eprintfn "Error: Not the leader, and current leader is unknown."

    let handleAddPeer (cmd: string) (node: RaftNode) =
        let parts = cmd.Split ' '

        if parts.Length = 4 then
            match System.Int32.TryParse parts.[1] with
            | true, peerId ->
                let peerInfo =
                    { Id = peerId
                      Host = parts.[2]
                      Port = System.Int32.Parse parts.[3] }

                match node.AddPeer peerInfo with
                | true -> printfn $"AddPeer {peerId} accepted (non-voting member)."
                | false -> printfn "AddPeer failed: only the leader can add peers."
            | _ -> eprintfn "Invalid peer ID."
        else
            printfn "Usage: add-peer <id> <host> <port>"

    let handleRemovePeer (cmd: string) (node: RaftNode) =
        let parts = cmd.Split ' '

        if parts.Length = 2 then
            match System.Int32.TryParse parts.[1] with
            | true, peerId ->
                match node.RemovePeer peerId with
                | true -> printfn $"RemovePeer {peerId} accepted."
                | false -> printfn "RemovePeer failed: only the leader can remove peers."
            | _ -> eprintfn "Invalid peer ID."
        else
            printfn "Usage: remove-peer <id>"

    [<TailCall>]
    let rec commandLoop node kvs kvsLock =
        printf "> "
        let input = System.Console.ReadLine()

        if isNull input then
            ()
        else
            match input.Trim() with
            | "quit"
            | "q" -> ()
            | "" -> commandLoop node kvs kvsLock
            | "state" ->
                printState node
                commandLoop node kvs kvsLock
            | "dump" ->
                printDump node
                commandLoop node kvs kvsLock
            | "log" ->
                printLog node
                commandLoop node kvs kvsLock
            | "cluster" ->
                printCluster node
                commandLoop node kvs kvsLock
            | "snapshot" ->
                node.SubmitTakeSnapshot(lock kvsLock (fun () -> System.Text.Json.JsonSerializer.Serialize kvs))
                printfn "Snapshot submitted."
                commandLoop node kvs kvsLock
            | "election" ->
                node.TriggerElectionTimeout()
                printfn "Election timeout triggered."
                commandLoop node kvs kvsLock
            | "heartbeat" ->
                node.TriggerHeartbeatTimeout()
                printfn "Heartbeat triggered."
                commandLoop node kvs kvsLock
            | cmd when cmd.StartsWith "get " ->
                getValue cmd node kvs kvsLock
                commandLoop node kvs kvsLock
            | cmd when cmd.StartsWith "put " ->
                submitCommand cmd node
                commandLoop node kvs kvsLock
            | cmd when cmd.StartsWith "add-peer " ->
                handleAddPeer cmd node
                commandLoop node kvs kvsLock
            | cmd when cmd.StartsWith "remove-peer " ->
                handleRemovePeer cmd node
                commandLoop node kvs kvsLock
            | _ ->
                printfn "Unknown command. Type 'help' for commands."
                commandLoop node kvs kvsLock

    [<TailCall>]
    let rec loopJoinCluster self initialPeers =
        if AdminListener.requestJoinCluster self initialPeers then
            printfn $"Node {self.Id} added to cluster as non-voting member (catch-up)."
        else
            printfn "Retrying join request..."
            loopJoinCluster self initialPeers

    let runNode nodeId port initialPeers =
        let kvsLock = obj ()
        let mutable kvs = Map.empty<string, string>

        let onApply entry =
            match entry.Command.Split ' ' with
            | [| "put"; k; v |] ->
                lock kvsLock (fun () -> kvs <- kvs |> Map.add k v)
                printfn $"\n>>> Applied [{entry.Index}:T{entry.Term}] put {k} = {v}"
            | _ -> printfn $"\n>>> Applied [{entry.Index}:T{entry.Term}]: {entry.Command}"

            printf "> "

        let onInstallSnapshot (data: string) =
            lock kvsLock (fun () -> kvs <- System.Text.Json.JsonSerializer.Deserialize<Map<string, string>> data)
            printfn $"\n>>> Snapshot installed with {lock kvsLock (fun () -> kvs.Count)} entries"
            printf "> "

        let onGetSnapshotData () =
            lock kvsLock (fun () -> System.Text.Json.JsonSerializer.Serialize kvs)

        let config = createConfig nodeId port initialPeers
        printfn $"Starting Node {nodeId} on port {config.Port}..."
        let transport = TcpTransport()
        let persistence = FilePersistence nodeId

        use node =
            new RaftNode(config, transport, persistence, onApply, onInstallSnapshot, onGetSnapshotData)

        AdminListener.startListener node config.Port |> Async.Start
        waitForPort config.Port 5000
        let isExistingMember = initialPeers |> List.exists (fun p -> p.Id = nodeId)

        if not isExistingMember then
            let self =
                { Id = nodeId
                  Host = "127.0.0.1"
                  Port = port }

            printfn $"Requesting to join cluster via {initialPeers.Length} known peer(s)..."
            loopJoinCluster self initialPeers

        printCommands ()
        commandLoop node kvs kvsLock

    type CliArgs =
        { NodeId: int
          Port: int
          InitialPeers: PeerInfo list }

    [<TailCall>]
    let rec loopParseArgv nodeId port initialPeers =
        function
        | "--node" :: idStr :: rest ->
            match System.Int32.TryParse idStr with
            | true, id -> loopParseArgv (Some id) port initialPeers rest
            | _ -> None
        | "--port" :: pStr :: rest ->
            match System.Int32.TryParse pStr with
            | true, p -> loopParseArgv nodeId (Some p) initialPeers rest
            | _ -> None
        | "--peer" :: idStr :: host :: portStr :: rest ->
            match System.Int32.TryParse idStr, System.Int32.TryParse portStr with
            | (true, pid), (true, p) ->
                let peer = { Id = pid; Host = host; Port = p }
                loopParseArgv nodeId port (peer :: initialPeers) rest
            | _ -> None
        | [] ->
            match nodeId, port with
            | Some nid, Some p ->
                Some
                    { NodeId = nid
                      Port = p
                      InitialPeers = List.rev initialPeers }
            | _ -> None
        | _ -> None

    let parseArgv = Array.toList >> loopParseArgv None None []

    [<EntryPoint>]
    let main argv =
        match parseArgv argv with
        | None ->
            printHelp ()
            1
        | Some cliArgs ->
            runNode cliArgs.NodeId cliArgs.Port cliArgs.InitialPeers
            0
