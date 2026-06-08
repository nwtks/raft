namespace Raft

module NodeLocal =
    let commitAndBroadcast ctx state onApplied =
        NodeUtil.saveIfChanged ctx state
        NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
        let appliedState = NodeApply.applyCommitted ctx.OnApply state
        onApplied appliedState

        NodeSnapshot.autoSnapshotIfNeeded ctx appliedState
        |> NodePromotion.tryFinalizeConfiguration

    let handleClientCommand ctx cmd clientId seqNum (replyChannel: AsyncReplyChannel<ClientCommandResult>) =
        if ctx.State.Role = Leader then
            match clientId, seqNum with
            | Some cId, Some sNum ->
                match ctx.State.Persistent.SessionTable |> Map.tryFind cId with
                | Some lastSeq when sNum <= lastSeq ->
                    replyChannel.Reply Accepted
                    ctx.State
                | _ ->
                    let state = Replication.appendCommandWithSession cmd cId sNum ctx.State
                    commitAndBroadcast ctx state (fun _ -> replyChannel.Reply Accepted)
            | _ ->
                let state = Replication.appendCommand cmd ctx.State
                commitAndBroadcast ctx state (fun _ -> replyChannel.Reply Accepted)
        else
            ctx.State.CurrentLeader
            |> Option.bind (fun leaderId -> ctx.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))
            |> Redirect
            |> replyChannel.Reply

            ctx.State

    let handleAddPeer ctx peerInfo (replyChannel: AsyncReplyChannel<bool>) =
        if ctx.State.Role = Leader then
            if
                ctx.State.Config.Peers |> List.exists (fun p -> p.Id = peerInfo.Id)
                || ctx.State.NonVotingPeers |> List.exists (fun p -> p.Id = peerInfo.Id)
            then
                replyChannel.Reply true
                ctx.State
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
                replyChannel.Reply true
                newState
        else
            replyChannel.Reply false
            ctx.State

    let commitAndBroadcastBool (ctx: NodeContext) state (replyChannel: AsyncReplyChannel<bool>) =
        NodeUtil.saveIfChanged ctx state
        NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
        let appliedState = NodeApply.applyCommitted ctx.OnApply state
        replyChannel.Reply true

        NodeSnapshot.autoSnapshotIfNeeded ctx appliedState
        |> NodePromotion.tryFinalizeConfiguration

    let handleRemovePeer ctx peerId (replyChannel: AsyncReplyChannel<bool>) =
        if ctx.State.Role = Leader then
            let oldPeers = ctx.State.Config.Peers
            let newPeers = oldPeers |> List.filter (fun p -> p.Id <> peerId)
            let state = Replication.appendJointConsensus oldPeers newPeers ctx.State
            commitAndBroadcast ctx state (fun _ -> replyChannel.Reply true)
        else
            replyChannel.Reply false
            ctx.State

    let handleLocalMessage ctx =
        function
        | ClientCommand(cmd, clientId, seqNum, replyChannel) -> handleClientCommand ctx cmd clientId seqNum replyChannel
        | AddPeer(peerInfo, replyChannel) -> handleAddPeer ctx peerInfo replyChannel
        | RemovePeer(peerId, replyChannel) -> handleRemovePeer ctx peerId replyChannel
        | _ ->
            NodeUtil.log $"Warning: unexpected message routed to handleLocalMessage, ignoring"
            ctx.State

    let handleLocalMessageResult ctx localMsg =
        let oldRole = ctx.State.Role
        let state = handleLocalMessage ctx localMsg
        let remainingReads = NodeRead.processPendingReads ctx.PendingReads state

        let electionTimer, heartbeatTimer =
            if oldRole <> Leader && state.Role = Leader then
                NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport state
                NodeTimer.stopTimer ctx.ElectionTimer
                ctx.ElectionTimer, NodeTimer.resetHeartbeatTimer ctx
            elif oldRole = Leader && state.Role <> Leader then
                NodeTimer.stopTimer ctx.HeartbeatTimer
                NodeTimer.resetElectionTimer ctx, ctx.HeartbeatTimer
            else
                ctx.ElectionTimer, ctx.HeartbeatTimer

        { ctx with
            State = state
            Config = state.Config
            ElectionTimer = electionTimer
            HeartbeatTimer = heartbeatTimer
            PendingReads = remainingReads }
