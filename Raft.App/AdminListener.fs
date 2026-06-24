namespace Raft.App

open Raft

module AdminListener =
    let adminPortOf (raftPort: int) = raftPort + 10000

    let parseRequest (node: RaftNode) (json: string) =
        use doc = System.Text.Json.JsonDocument.Parse json
        let typ = doc.RootElement.GetProperty("type").GetString()
        let id = doc.RootElement.GetProperty("id").GetInt32()
        let host = doc.RootElement.GetProperty("host").GetString()
        let p = doc.RootElement.GetProperty("port").GetInt32()
        let peerInfo = { Id = id; Host = host; Port = p }

        match typ with
        | "add-peer" ->
            match node.AddPeer peerInfo with
            | true -> Some """{"status":"ok"}"""
            | false -> Some """{"status":"error","message":"not leader"}"""
        | _ -> None

    let accept (node: RaftNode) (client: System.Net.Sockets.TcpClient) =
        async {
            try
                use stream = client.GetStream()
                use reader = new System.IO.StreamReader(stream)
                use writer = new System.IO.StreamWriter(stream)
                let! line = reader.ReadLineAsync() |> Async.AwaitTask

                match line with
                | null -> ()
                | json ->
                    match parseRequest node json with
                    | Some response -> do! writer.WriteLineAsync response |> Async.AwaitTask
                    | None -> ()
            with _ ->
                ()
        }

    let rec listen (node: RaftNode) (listener: System.Net.Sockets.TcpListener) =
        async {
            let! client = listener.AcceptTcpClientAsync() |> Async.AwaitTask
            accept node client |> Async.Start
            return! listen node listener
        }

    let startListener (node: RaftNode) (raftPort: int) =
        let port = adminPortOf raftPort

        let listener =
            new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port)

        listener.Start()
        printfn $"Admin listener on port {port}"
        listen node listener

    let sendAddMe (self: PeerInfo) (peerHost: string) (adminPort: int) =
        async {
            try
                use client = new System.Net.Sockets.TcpClient()
                do! client.ConnectAsync(peerHost, adminPort) |> Async.AwaitTask
                use stream = client.GetStream()
                use writer = new System.IO.StreamWriter(stream)

                let request =
                    $"""{{"type":"add-peer","id":{self.Id},"host":"{self.Host}","port":{self.Port}}}"""

                do! writer.WriteLineAsync request |> Async.AwaitTask
                do! writer.FlushAsync() |> Async.AwaitTask
                use reader = new System.IO.StreamReader(stream)
                let! response = reader.ReadLineAsync() |> Async.AwaitTask
                return response
            with ex ->
                return $"""{{"status":"error","message":"{ex.Message}"}}"""
        }

    [<TailCall>]
    let rec requestJoinCluster (self: PeerInfo) (knownPeers: PeerInfo list) =
        match knownPeers with
        | [] ->
            printfn "No peers to contact. Will retry in 3s..."
            System.Threading.Thread.Sleep 3000
            false
        | peer :: rest ->
            let adminPort = adminPortOf peer.Port
            printfn $"  Contacting {peer.Id} ({peer.Host}:{adminPort})..."
            let response = sendAddMe self peer.Host adminPort |> Async.RunSynchronously

            let status =
                try
                    use doc = System.Text.Json.JsonDocument.Parse response
                    doc.RootElement.GetProperty("status").GetString() |> Some
                with _ ->
                    None

            match status with
            | Some "ok" -> true
            | _ ->
                let reason =
                    match status with
                    | Some _ -> "not ready yet"
                    | None -> "failed to contact"

                printfn $"  -> {reason}, trying next peer..."
                requestJoinCluster self rest
