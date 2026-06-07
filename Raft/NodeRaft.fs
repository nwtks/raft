namespace Raft

module NodeRaft =
    let handleRaftMessage ctx =
        function
        | RequestVoteMsg requestVote ->
            let state, response = Election.handleRequestVote requestVote ctx.State
            NodeUtil.saveIfChanged ctx state

            match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = requestVote.CandidateId) with
            | Some peer -> NodeUtil.sendAsync ctx.Transport peer (RequestVoteResponseMsg response)
            | None -> NodeUtil.log $"Warning: Unknown candidate {requestVote.CandidateId} for RequestVote response"

            state, true
        | RequestVoteResponseMsg response ->
            let state = Election.handleVoteResponse response.VoterId response ctx.State
            NodeUtil.saveIfChanged ctx state
            state, false
        | AppendEntriesMsg appendEntries ->
            let state, response = Replication.handleAppendEntries appendEntries ctx.State
            NodeUtil.saveIfChanged ctx state

            match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = appendEntries.LeaderId) with
            | Some peer -> NodeUtil.sendAsync ctx.Transport peer (AppendEntriesResponseMsg response)
            | None -> NodeUtil.log $"Warning: Unknown leader {appendEntries.LeaderId} for AppendEntries response"

            state, true
        | AppendEntriesResponseMsg response ->
            let state =
                Replication.handleAppendEntriesResponse response ctx.State
                |> Replication.advanceCommitIndex
                |> NodePromotion.tryPromoteNonVotingPeers ctx

            NodeUtil.saveIfChanged ctx state
            state, false
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

            match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = snap.LeaderId) with
            | Some peer -> NodeUtil.sendAsync ctx.Transport peer (InstallSnapshotResponseMsg response)
            | None -> NodeUtil.log $"Warning: Unknown leader {snap.LeaderId} for InstallSnapshot response"

            state, true
        | InstallSnapshotResponseMsg response ->
            let state =
                Replication.handleInstallSnapshotResponse response ctx.State
                |> Replication.advanceCommitIndex

            NodeUtil.saveIfChanged ctx state
            state, false

    let receiveRaftRPC ctx rpcMsg =
        let oldRole = ctx.State.Role
        let state, sendReply = handleRaftMessage ctx rpcMsg

        let newState =
            NodeApply.applyCommitted ctx.OnApply state
            |> NodePromotion.autoSnapshotIfNeeded ctx
            |> NodePromotion.tryFinalizeConfiguration

        let electionTimer, heartbeatTimer =
            if oldRole <> Leader && newState.Role = Leader then
                NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport newState
                NodeTimer.stopTimer ctx.ElectionTimer
                ctx.ElectionTimer, NodeTimer.resetHeartbeatTimer ctx
            elif oldRole = Leader && newState.Role <> Leader then
                NodeTimer.stopTimer ctx.HeartbeatTimer
                NodeTimer.resetElectionTimer ctx, ctx.HeartbeatTimer
            elif sendReply then
                NodeTimer.resetElectionTimer ctx, ctx.HeartbeatTimer
            else
                ctx.ElectionTimer, ctx.HeartbeatTimer

        newState, electionTimer, heartbeatTimer

    let handleRaftRPCResult ctx rpcMsg state electionTimer heartbeatTimer =
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

        let remainingReads = NodeApply.processPendingReads updatedReads state

        let newCtx =
            { ctx with
                State = state
                ElectionTimer = electionTimer
                HeartbeatTimer = heartbeatTimer
                PendingReads = remainingReads }

        if state.Config <> ctx.Config then
            { newCtx with Config = state.Config }
        else
            newCtx
