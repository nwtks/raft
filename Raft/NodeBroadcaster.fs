namespace Raft

module NodeBroadcaster =
    let broadcastRequestVote config (transport: ITransport) state =
        let requestVote = Election.createRequestVote state

        for peer in config.Peers do
            transport.SendMessage peer (RequestVoteMsg requestVote)

    let broadcastHeartbeat config (transport: ITransport) state =
        for peer in config.Peers do
            match Replication.createHeartbeat peer.Id state with
            | Some hb -> transport.SendMessage peer (AppendEntriesMsg hb)
            | None -> ()

    let broadcastAppendEntries config (transport: ITransport) state =
        for peer in config.Peers do
            match Replication.createAppendEntries peer.Id state with
            | Some ae -> transport.SendMessage peer (AppendEntriesMsg ae)
            | None -> ()
