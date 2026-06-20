namespace Raft

module NodeRaft =
    let sendResponse ctx peerId msg =
        match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = peerId) with
        | Some peer -> NodeUtil.sendAsync ctx.Transport peer msg
        | None -> NodeUtil.log $"Warning: Unknown peer {peerId} — cannot send response"

    let handleRequestVote ctx msg =
        let state, response = Election.handleRequestVote msg ctx.State
        NodeUtil.saveIfChanged ctx state
        sendResponse ctx msg.CandidateId (RequestVoteResponseMsg response)
        state, true, None

    let handleRequestVoteResponse ctx msg =
        let state = Election.handleVoteResponse msg.VoterId msg ctx.State
        NodeUtil.saveIfChanged ctx state
        state, false, None

    let handleAppendEntries ctx msg =
        let state, response = Replication.handleAppendEntries msg ctx.State
        NodeUtil.saveIfChanged ctx state
        sendResponse ctx msg.LeaderId (AppendEntriesResponseMsg response)
        state, true, None

    let handleAppendEntriesResponse ctx msg =
        let state =
            Replication.handleAppendEntriesResponse msg ctx.State
            |> Replication.advanceCommitIndex
            |> NodePromotion.tryPromoteNonVotingPeers ctx

        NodeUtil.saveIfChanged ctx state
        state, false, None

    let handleInstallSnapshot ctx snap =
        let state, response = Replication.handleInstallSnapshot snap ctx.State
        NodeUtil.saveIfChanged ctx state
        sendResponse ctx snap.LeaderId (InstallSnapshotResponseMsg response)

        let afterEffect =
            if response.Success then
                async {
                    try
                        ctx.OnInstallSnapshot snap.Data
                    with ex ->
                        NodeUtil.log
                            $"CRITICAL: Snapshot apply failed at index {snap.LastIncludedIndex}: {ex.Message}. Node may require restart."
                }
                |> Some
            else
                None

        state, true, afterEffect

    let handleInstallSnapshotResponse ctx msg =
        let state =
            Replication.handleInstallSnapshotResponse msg ctx.State
            |> Replication.advanceCommitIndex

        NodeUtil.saveIfChanged ctx state
        state, false, None

    let handleRaftMessage ctx rpcMsg =
        match rpcMsg with
        | RequestVoteMsg msg -> handleRequestVote ctx msg
        | RequestVoteResponseMsg msg -> handleRequestVoteResponse ctx msg
        | AppendEntriesMsg msg -> handleAppendEntries ctx msg
        | AppendEntriesResponseMsg msg -> handleAppendEntriesResponse ctx msg
        | InstallSnapshotMsg snap -> handleInstallSnapshot ctx snap
        | InstallSnapshotResponseMsg msg -> handleInstallSnapshotResponse ctx msg

    let updatePendingReads ctx state rpcMsg =
        let updatedReads =
            match rpcMsg with
            | AppendEntriesResponseMsg resp when
                state.Role = Leader && resp.FollowerTerm <= state.Persistent.CurrentTerm
                ->
                ctx.PendingReads
                |> List.map (fun pendingRead ->
                    { pendingRead with
                        Responses = Set.add resp.FollowerId pendingRead.Responses })
            | _ -> ctx.PendingReads

        NodeRead.processPendingReads updatedReads state

    let handleRaftRPC ctx rpcMsg =
        let oldRole = ctx.State.Role
        let state, sendReply, afterEffect = handleRaftMessage ctx rpcMsg
        afterEffect |> Option.iter Async.Start

        let newState =
            NodeApply.applyCommitted ctx.OnApply state
            |> NodeSnapshot.autoSnapshotIfNeeded ctx
            |> NodePromotion.tryFinalizeConfiguration

        if oldRole <> Leader && newState.Role = Leader then
            NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport newState

        let electionAction, heartbeatAction =
            NodeTimer.getTimerActionsOnRoleChange oldRole newState sendReply

        let remainingReads = updatePendingReads ctx newState rpcMsg

        { State = newState
          ElectionAction = electionAction
          HeartbeatAction = heartbeatAction
          PendingReads = remainingReads }
