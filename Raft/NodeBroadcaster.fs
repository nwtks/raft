namespace Raft

module NodeBroadcaster =
    let log msg = printfn "[Node] %s" msg

    let sendAsync (transport: ITransport) peer msg =
        async {
            try
                do! transport.SendMessage peer msg |> Async.AwaitTask
            with ex ->
                log $"Failed to send to {peer.Id}: {ex.Message}"
        }
        |> Async.Start

    let broadcastRequestVote config (transport: ITransport) state =
        let requestVote = Election.createRequestVote state

        for peer in config.Peers do
            sendAsync transport peer (RequestVoteMsg requestVote)

    let broadcastHeartbeat config (transport: ITransport) state =
        for peer in config.Peers do
            match Replication.createHeartbeat peer.Id state with
            | Some hb -> sendAsync transport peer (AppendEntriesMsg hb)
            | None -> ()

    let broadcastAppendEntries config (transport: ITransport) state =
        for peer in config.Peers do
            match Replication.createAppendEntries peer.Id state with
            | Some ae -> sendAsync transport peer (AppendEntriesMsg ae)
            | None -> ()
