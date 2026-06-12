namespace Raft

module NodeLocal =
    let commitAndBroadcast ctx state =
        NodeUtil.saveIfChanged ctx state
        NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
        let appliedState = NodeApply.applyCommitted ctx.OnApply state

        NodeSnapshot.autoSnapshotIfNeeded ctx appliedState
        |> NodePromotion.tryFinalizeConfiguration

    let handleClientCommand ctx cmd clientId seqNum : RaftState * ClientCommandResult =
        if ctx.State.Role = Leader then
            match clientId, seqNum with
            | Some cId, Some sNum ->
                match ctx.State.Persistent.SessionTable |> Map.tryFind cId with
                | Some lastSeq when sNum <= lastSeq -> ctx.State, Accepted
                | _ ->
                    let state = Replication.appendCommandWithSession cmd cId sNum ctx.State
                    commitAndBroadcast ctx state, Accepted
            | _ ->
                let state = Replication.appendCommand cmd ctx.State
                commitAndBroadcast ctx state, Accepted
        else
            let result =
                ctx.State.CurrentLeader
                |> Option.bind (fun leaderId -> ctx.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))
                |> Redirect

            ctx.State, result

    let handleAddPeer ctx peerInfo : RaftState * bool =
        if ctx.State.Role = Leader then
            if
                ctx.State.Config.Peers |> List.exists (fun p -> p.Id = peerInfo.Id)
                || ctx.State.NonVotingPeers |> List.exists (fun p -> p.Id = peerInfo.Id)
            then
                ctx.State, true
            else
                let state =
                    { ctx.State with
                        NonVotingPeers = peerInfo :: ctx.State.NonVotingPeers }

                let lastIdx = Log.lastIndex state.Persistent.Log

                let newState =
                    match state.LeaderState with
                    | Some ls ->
                        { state with
                            LeaderState =
                                Some
                                    { ls with
                                        NextIndex = ls.NextIndex |> Map.add peerInfo.Id (lastIdx + 1L)
                                        MatchIndex = ls.MatchIndex |> Map.add peerInfo.Id Log.initialLogIndex } }
                    | None -> state

                NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport newState
                newState, true
        else
            ctx.State, false

    let handleRemovePeer ctx peerId : RaftState * bool =
        if ctx.State.Role = Leader then
            let oldPeers = ctx.State.Config.Peers
            let newPeers = oldPeers |> List.filter (fun p -> p.Id <> peerId)
            let state = Replication.appendJointConsensus oldPeers newPeers ctx.State
            commitAndBroadcast ctx state, true
        else
            ctx.State, false

    let dispatchLocalMessage ctx =
        function
        | ClientCommand(cmd, clientId, seqNum, replyChannel) ->
            let state, result = handleClientCommand ctx cmd clientId seqNum
            replyChannel.Reply result
            state
        | AddPeer(peerInfo, replyChannel) ->
            let state, result = handleAddPeer ctx peerInfo
            replyChannel.Reply result
            state
        | RemovePeer(peerId, replyChannel) ->
            let state, result = handleRemovePeer ctx peerId
            replyChannel.Reply result
            state
        | _ ->
            NodeUtil.log $"Warning: unexpected message routed to handleLocalMessage, ignoring"
            ctx.State

    let handleLocalMessage ctx localMsg =
        let oldRole = ctx.State.Role
        let state = dispatchLocalMessage ctx localMsg
        let remainingReads = NodeRead.processPendingReads ctx.PendingReads state

        let electionAction, heartbeatAction =
            NodeTimer.getTimerActionsOnRoleChange ctx oldRole state false

        { State = state
          ElectionAction = electionAction
          HeartbeatAction = heartbeatAction
          PendingReads = remainingReads }
