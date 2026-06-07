namespace Raft

module NodeBroadcaster =
    let log msg = printfn "[Node] %s" msg

    let sendAsync (transport: ITransport) peer msg =
        try
            let task = transport.SendMessage peer msg

            task.ContinueWith(fun (t: System.Threading.Tasks.Task) ->
                if t.IsFaulted then
                    let exMsg =
                        match t.Exception with
                        | null -> "(unknown)"
                        | ae -> ae.GetBaseException().Message

                    log $"Failed to send to {peer.Id}: {exMsg}")
            |> ignore
        with ex ->
            log $"Failed to send to {peer.Id}: {ex.Message}"

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
            | None ->
                match Replication.createInstallSnapshot peer.Id state with
                | Some snap -> sendAsync transport peer (InstallSnapshotMsg snap)
                | None -> ()

        for peer in state.NonVotingPeers do
            match Replication.createAppendEntries peer.Id state with
            | Some ae -> sendAsync transport peer (AppendEntriesMsg ae)
            | None ->
                match Replication.createInstallSnapshot peer.Id state with
                | Some snap -> sendAsync transport peer (InstallSnapshotMsg snap)
                | None -> ()
