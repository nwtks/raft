namespace Raft

module NodeLocal =
    let appendAsLeader ctx cmd clientId seqNum =
        let isDuplicate =
            State.isDuplicateSession ctx.State.Persistent.SessionTable clientId seqNum

        if isDuplicate then
            ctx.State, Accepted
        else
            let state =
                match clientId, seqNum with
                | Some cId, Some sNum -> Replication.appendCommandWithSession cmd cId sNum ctx.State
                | _ -> Replication.appendCommand cmd ctx.State

            NodeUtil.saveIfChanged ctx state
            NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
            let appliedState = NodeApply.applyCommitted ctx.OnApply state

            let finalState =
                NodeSnapshot.autoSnapshotIfNeeded ctx appliedState
                |> NodePromotion.tryFinalizeConfiguration

            finalState, Accepted

    let redirectResult ctx =
        ctx.State.CurrentLeader
        |> Option.bind (fun leaderId -> ctx.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))
        |> Redirect

    let handleClientCommand ctx cmd clientId seqNum =
        if ctx.State.Role = Leader then
            appendAsLeader ctx cmd clientId seqNum
        else
            ctx.State, redirectResult ctx

    let isExistingPeer (state: RaftState) peerId =
        state.Config.Peers |> List.exists (fun p -> p.Id = peerId)
        || state.NonVotingPeers |> List.exists (fun p -> p.Id = peerId)

    let extendLeaderStateForPeer (state: RaftState) peerId lastIdx =
        match state.LeaderState with
        | Some ls ->
            { state with
                LeaderState =
                    Some
                        { ls with
                            NextIndex = ls.NextIndex |> Map.add peerId (lastIdx + 1L)
                            MatchIndex = ls.MatchIndex |> Map.add peerId Log.initialLogIndex } }
        | None -> state

    let handleAddPeer ctx peerInfo =
        if ctx.State.Role <> Leader then
            ctx.State, false
        elif isExistingPeer ctx.State peerInfo.Id then
            ctx.State, true
        else
            let state =
                { ctx.State with
                    NonVotingPeers = peerInfo :: ctx.State.NonVotingPeers }

            let newState =
                Log.lastIndex state.Persistent.Log |> extendLeaderStateForPeer state peerInfo.Id

            NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport newState
            newState, true

    let handleRemovePeer ctx peerId =
        if ctx.State.Role = Leader then
            let oldPeers = ctx.State.Config.Peers
            let newPeers = oldPeers |> List.filter (fun p -> p.Id <> peerId)
            let state = Replication.appendJointConsensus oldPeers newPeers ctx.State
            NodeUtil.saveIfChanged ctx state
            NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
            let appliedState = NodeApply.applyCommitted ctx.OnApply state

            let finalState =
                NodeSnapshot.autoSnapshotIfNeeded ctx appliedState
                |> NodePromotion.tryFinalizeConfiguration

            finalState, true
        else
            ctx.State, false

    let dispatchLocalMessage ctx =
        function
        | ClientCommand(cmd, clientId, seqNum, reply) ->
            let state, result = handleClientCommand ctx cmd clientId seqNum
            reply result
            state
        | AddPeer(peerInfo, reply) ->
            let state, result = handleAddPeer ctx peerInfo
            reply result
            state
        | RemovePeer(peerId, reply) ->
            let state, result = handleRemovePeer ctx peerId
            reply result
            state
        | _ ->
            NodeUtil.log $"Warning: unexpected message routed to handleLocalMessage, ignoring"
            ctx.State

    let handleLocalMessage ctx localMsg =
        let oldRole = ctx.State.Role
        let state = dispatchLocalMessage ctx localMsg
        let remainingReads = NodeRead.processPendingReads ctx.PendingReads state

        let electionAction, heartbeatAction =
            NodeTimer.getTimerActionsOnRoleChange oldRole state false

        { State = state
          ElectionAction = electionAction
          HeartbeatAction = heartbeatAction
          PendingReads = remainingReads }
