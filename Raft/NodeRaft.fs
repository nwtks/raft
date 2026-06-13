namespace Raft

module NodeRaft =
    let sendResponse ctx peerId msg =
        match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = peerId) with
        | Some peer -> NodeUtil.sendAsync ctx.Transport peer msg
        | None -> NodeUtil.log $"Warning: Unknown peer {peerId} — cannot send response"

    let handleRaftMessage ctx =
        let reply peerId wrapper (state, response) =
            NodeUtil.saveIfChanged ctx state
            sendResponse ctx peerId (wrapper response)
            state, true, None

        let noReply state =
            NodeUtil.saveIfChanged ctx state
            state, false, None

        function
        | RequestVoteMsg msg ->
            Election.handleRequestVote msg ctx.State
            |> reply msg.CandidateId RequestVoteResponseMsg
        | RequestVoteResponseMsg msg -> noReply (Election.handleVoteResponse msg.VoterId msg ctx.State)
        | AppendEntriesMsg msg ->
            Replication.handleAppendEntries msg ctx.State
            |> reply msg.LeaderId AppendEntriesResponseMsg
        | AppendEntriesResponseMsg msg ->
            Replication.handleAppendEntriesResponse msg ctx.State
            |> Replication.advanceCommitIndex
            |> NodePromotion.tryPromoteNonVotingPeers ctx
            |> noReply
        | InstallSnapshotMsg snap ->
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
        | InstallSnapshotResponseMsg msg ->
            Replication.handleInstallSnapshotResponse msg ctx.State
            |> Replication.advanceCommitIndex
            |> noReply

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

        let electionAction, heartbeatAction =
            NodeTimer.getTimerActionsOnRoleChange ctx oldRole newState sendReply

        let remainingReads = updatePendingReads ctx newState rpcMsg

        { State = newState
          ElectionAction = electionAction
          HeartbeatAction = heartbeatAction
          PendingReads = remainingReads }
