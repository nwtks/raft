namespace Raft

module NodeRaft =
    let sendResponse ctx peerId msg =
        match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = peerId) with
        | Some peer -> NodeUtil.sendAsync ctx.Transport peer msg
        | None -> NodeUtil.log $"Warning: Unknown peer {peerId} — cannot send response"

    let handleRaftMessage ctx =
        let reply (state, response) peerId wrapper =
            NodeUtil.saveIfChanged ctx state
            sendResponse ctx peerId (wrapper response)
            state, true

        let noReply state =
            NodeUtil.saveIfChanged ctx state
            state, false

        function
        | RequestVoteMsg msg -> reply (Election.handleRequestVote msg ctx.State) msg.CandidateId RequestVoteResponseMsg
        | RequestVoteResponseMsg msg -> noReply (Election.handleVoteResponse msg.VoterId msg ctx.State)
        | AppendEntriesMsg msg ->
            reply (Replication.handleAppendEntries msg ctx.State) msg.LeaderId AppendEntriesResponseMsg
        | AppendEntriesResponseMsg msg ->
            noReply (
                Replication.handleAppendEntriesResponse msg ctx.State
                |> Replication.advanceCommitIndex
                |> NodePromotion.tryPromoteNonVotingPeers ctx
            )
        | InstallSnapshotMsg snap ->
            let state, response = Replication.handleInstallSnapshot snap ctx.State
            NodeUtil.saveIfChanged ctx state

            if response.Success then
                async {
                    try
                        ctx.OnInstallSnapshot snap.Data
                    with ex ->
                        NodeUtil.log
                            $"CRITICAL: Snapshot apply failed at index {snap.LastIncludedIndex}: {ex.Message}. Node may require restart."
                }
                |> Async.Start

            sendResponse ctx snap.LeaderId (InstallSnapshotResponseMsg response)
            state, true
        | InstallSnapshotResponseMsg msg ->
            noReply (
                Replication.handleInstallSnapshotResponse msg ctx.State
                |> Replication.advanceCommitIndex
            )

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
        let state, sendReply = handleRaftMessage ctx rpcMsg

        let newState =
            NodeApply.applyCommitted ctx.OnApply state
            |> NodeSnapshot.autoSnapshotIfNeeded ctx
            |> NodePromotion.tryFinalizeConfiguration

        let electionAction, heartbeatAction =
            NodeTimer.getTimerActionsOnRoleChange ctx oldRole newState sendReply

        let remainingReads = updatePendingReads ctx newState rpcMsg

        { State = newState
          ElectionAction = electionAction
          HeartbeatAction = heartbeatAction
          PendingReads = remainingReads }
