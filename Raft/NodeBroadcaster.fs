namespace Raft

module NodeBroadcaster =

    let broadcastRequestVote config (transport: ITransport) state =
        let requestVote = Election.createRequestVote state

        for peer in config.Peers do
            NodeUtil.sendAsync transport peer (RequestVoteMsg requestVote)

    let broadcastHeartbeat config (transport: ITransport) state =
        for peer in config.Peers do
            match Replication.createHeartbeat peer.Id state with
            | Some hb -> NodeUtil.sendAsync transport peer (AppendEntriesMsg hb)
            | None -> ()

    let sendAppendEntriesOrSnapshot (transport: ITransport) peer state =
        match Replication.createAppendEntries peer.Id state with
        | Some ae -> NodeUtil.sendAsync transport peer (AppendEntriesMsg ae)
        | None ->
            match Replication.createInstallSnapshot peer.Id state with
            | Some snap -> NodeUtil.sendAsync transport peer (InstallSnapshotMsg snap)
            | None -> ()

    let broadcastAppendEntries config (transport: ITransport) state =
        for peer in config.Peers do
            sendAppendEntriesOrSnapshot transport peer state

        for peer in state.NonVotingPeers do
            sendAppendEntriesOrSnapshot transport peer state
