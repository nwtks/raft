namespace Raft

module NodeRaft =
    let sendResponse ctx peerId msg =
        match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = peerId) with
        | Some peer -> NodeUtil.sendAsync ctx.Transport peer msg
        | None -> NodeUtil.log $"Warning: Unknown peer {peerId} — cannot send response"

    let handleRaftMessage ctx =
        function
        | RequestVoteMsg requestVote ->
            let state, response = Election.handleRequestVote requestVote ctx.State
            NodeUtil.saveIfChanged ctx state
            sendResponse ctx requestVote.CandidateId (RequestVoteResponseMsg response)
            state, true
        | RequestVoteResponseMsg response ->
            let state = Election.handleVoteResponse response.VoterId response ctx.State
            NodeUtil.saveIfChanged ctx state
            state, false
        | AppendEntriesMsg appendEntries ->
            let state, response = Replication.handleAppendEntries appendEntries ctx.State
            NodeUtil.saveIfChanged ctx state
            sendResponse ctx appendEntries.LeaderId (AppendEntriesResponseMsg response)
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

            sendResponse ctx snap.LeaderId (InstallSnapshotResponseMsg response)
            state, true
        | InstallSnapshotResponseMsg response ->
            let state =
                Replication.handleInstallSnapshotResponse response ctx.State
                |> Replication.advanceCommitIndex

            NodeUtil.saveIfChanged ctx state
            state, false

    let handleRaftRPC ctx rpcMsg =
        let oldRole = ctx.State.Role
        let state, sendReply = handleRaftMessage ctx rpcMsg

        let newState =
            NodeApply.applyCommitted ctx.OnApply state
            |> NodeSnapshot.autoSnapshotIfNeeded ctx
            |> NodePromotion.tryFinalizeConfiguration

        let electionAction, heartbeatAction =
            NodeTimer.getTimerActionsOnRoleChange ctx oldRole newState sendReply

        let updatedReads =
            match rpcMsg with
            | AppendEntriesResponseMsg resp when
                newState.Role = Leader && resp.FollowerTerm <= newState.Persistent.CurrentTerm
                ->
                ctx.PendingReads
                |> List.map (fun pendingRead ->
                    { pendingRead with
                        Responses = Set.add resp.FollowerId pendingRead.Responses })
            | _ -> ctx.PendingReads

        let remainingReads = NodeRead.processPendingReads updatedReads newState

        { State = newState
          ElectionAction = electionAction
          HeartbeatAction = heartbeatAction
          PendingReads = remainingReads }
