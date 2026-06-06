namespace Raft

module NodeAgent =
    let log msg = printfn "[Node] %s" msg

    [<TailCall>]
    let rec loopApplyCommitted onApply state lastApplied =
        if lastApplied < state.Volatile.CommitIndex then
            let next = lastApplied + 1L

            match Log.getEntry next state.Persistent.Log with
            | Some entry when entry.Command.StartsWith ConfigChange.ConfigCommandPrefix ->
                match ConfigChange.parse entry.Command with
                | Some(JointChange(oldPeers, newPeers)) ->
                    let state2 = State.enterJointConsensus oldPeers newPeers state
                    loopApplyCommitted onApply state2 next
                | Some(FinalChange newPeers) ->
                    let state2 = State.exitJointConsensus newPeers state
                    loopApplyCommitted onApply state2 next
                | None ->
                    let json = entry.Command.Substring ConfigChange.ConfigCommandPrefix.Length
                    let newPeers = System.Text.Json.JsonSerializer.Deserialize<PeerInfo list> json
                    let state2 = State.updateConfig newPeers state
                    loopApplyCommitted onApply state2 next
            | Some entry ->
                onApply entry
                loopApplyCommitted onApply state next
            | None -> loopApplyCommitted onApply state (next + 1L)
        else
            state, lastApplied

    let applyCommitted onApply state =
        let state2, lastApplied =
            loopApplyCommitted onApply state state.Volatile.LastApplied

        State.updateLastApplied lastApplied state2

    let saveIfChanged ctx newState =
        if ctx.State.Persistent <> newState.Persistent then
            ctx.Persistence.Save newState.Persistent

    let receiveElectionTimeout ctx =
        if ctx.State.Role <> Leader then
            let newState = Election.startElection ctx.State
            saveIfChanged ctx newState
            NodeBroadcaster.broadcastRequestVote ctx.Config ctx.Transport newState

            let finalState =
                if State.hasQuorum newState.VotesReceived newState then
                    let state = State.initLeaderState newState
                    NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport state
                    state
                else
                    newState

            finalState, NodeTimer.resetElectionTimer ctx
        else
            ctx.State, ctx.ElectionTimer

    let receiveHeartbeatTimeout ctx =
        if ctx.State.Role = Leader then
            NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport ctx.State
            ctx.State, NodeTimer.resetHeartbeatTimer ctx
        else
            ctx.State, ctx.HeartbeatTimer

    let handleRaftMessage ctx =
        function
        | RequestVoteMsg requestVote ->
            let state, response = Election.handleRequestVote requestVote ctx.State
            saveIfChanged ctx state

            match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = requestVote.CandidateId) with
            | Some peer -> NodeBroadcaster.sendAsync ctx.Transport peer (RequestVoteResponseMsg response)
            | None -> log $"Warning: Unknown candidate {requestVote.CandidateId} for RequestVote response"

            state, true
        | RequestVoteResponseMsg response ->
            let state = Election.handleVoteResponse response.VoterId response ctx.State
            saveIfChanged ctx state
            state, false
        | AppendEntriesMsg appendEntries ->
            let state, response = Replication.handleAppendEntries appendEntries ctx.State
            saveIfChanged ctx state

            match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = appendEntries.LeaderId) with
            | Some peer -> NodeBroadcaster.sendAsync ctx.Transport peer (AppendEntriesResponseMsg response)
            | None -> log $"Warning: Unknown leader {appendEntries.LeaderId} for AppendEntries response"

            state, true
        | AppendEntriesResponseMsg response ->
            let state = Replication.handleAppendEntriesResponse response ctx.State
            let state2 = Replication.advanceCommitIndex state
            saveIfChanged ctx state2
            state2, false
        | InstallSnapshotMsg snap ->
            let state, response = Replication.handleInstallSnapshot snap ctx.State
            saveIfChanged ctx state

            if response.Success then
                let snapData = snap.Data

                async {
                    try
                        ctx.OnInstallSnapshot snapData
                    with ex ->
                        log
                            $"CRITICAL: Snapshot apply failed at index {snap.LastIncludedIndex}: {ex.Message}. Node may require restart."
                }
                |> Async.Start

            match ctx.Config.Peers |> List.tryFind (fun p -> p.Id = snap.LeaderId) with
            | Some peer -> NodeBroadcaster.sendAsync ctx.Transport peer (InstallSnapshotResponseMsg response)
            | None -> log $"Warning: Unknown leader {snap.LeaderId} for InstallSnapshot response"

            state, true
        | InstallSnapshotResponseMsg response ->
            let state = Replication.handleInstallSnapshotResponse response ctx.State
            let state2 = Replication.advanceCommitIndex state
            saveIfChanged ctx state2
            state2, false

    let tryFinalizeConfiguration state =
        match state.ConfigPhase, state.Role with
        | JointPhase(_, newPeers), Leader ->
            let lastEntry =
                Log.getEntry (Log.lastIndex state.Persistent.Log) state.Persistent.Log

            match lastEntry with
            | Some entry when entry.Command.StartsWith ConfigChange.ConfigCommandPrefix ->
                match ConfigChange.parse entry.Command with
                | Some(FinalChange _) -> state
                | _ -> Replication.appendFinalConfiguration newPeers state
            | _ -> Replication.appendFinalConfiguration newPeers state
        | _ -> state

    let handleLocalMessage ctx =
        function
        | ClientCommand(cmd, replyChannel) ->
            if ctx.State.Role = Leader then
                let state = Replication.appendCommand cmd ctx.State
                saveIfChanged ctx state
                NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
                replyChannel.Reply true
                tryFinalizeConfiguration (applyCommitted ctx.OnApply state)
            else
                replyChannel.Reply false
                ctx.State
        | AddPeer(peerInfo, replyChannel) ->
            if ctx.State.Role = Leader then
                let oldPeers = ctx.State.Config.Peers

                let newPeers =
                    if oldPeers |> List.exists (fun p -> p.Id = peerInfo.Id) then
                        oldPeers
                    else
                        peerInfo :: oldPeers

                let state = Replication.appendJointConsensus oldPeers newPeers ctx.State
                saveIfChanged ctx state

                let state2 =
                    match state.LeaderState with
                    | Some ls ->
                        let lastIdx = Log.lastIndex state.Persistent.Log

                        { state with
                            LeaderState =
                                Some
                                    { ls with
                                        NextIndex = ls.NextIndex |> Map.add peerInfo.Id (lastIdx + 1L)
                                        MatchIndex = ls.MatchIndex |> Map.add peerInfo.Id 0L } }
                    | None -> state

                NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state2
                replyChannel.Reply true
                tryFinalizeConfiguration (applyCommitted ctx.OnApply state2)
            else
                replyChannel.Reply false
                ctx.State
        | RemovePeer(peerId, replyChannel) ->
            if ctx.State.Role = Leader then
                let oldPeers = ctx.State.Config.Peers
                let newPeers = oldPeers |> List.filter (fun p -> p.Id <> peerId)
                let state = Replication.appendJointConsensus oldPeers newPeers ctx.State
                saveIfChanged ctx state
                NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
                replyChannel.Reply true
                tryFinalizeConfiguration (applyCommitted ctx.OnApply state)
            else
                replyChannel.Reply false
                ctx.State
        | _ ->
            log $"Warning: unexpected message routed to handleLocalMessage, ignoring"
            ctx.State

    let receiveRaftRPC ctx rpcMsg =
        let oldRole = ctx.State.Role
        let newState, sendReply = handleRaftMessage ctx rpcMsg

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

        let appliedState = applyCommitted ctx.OnApply newState
        tryFinalizeConfiguration appliedState, electionTimer, heartbeatTimer

    [<TailCall>]
    let rec agentLoop ctx =
        async {
            let! msg = ctx.Inbox.Receive()

            match msg with
            | ElectionTimeout ->
                let state, electionTimer = receiveElectionTimeout ctx

                return!
                    agentLoop
                        { ctx with
                            State = state
                            ElectionTimer = electionTimer }
            | HeartbeatTimeout ->
                let state, heartbeatTimer = receiveHeartbeatTimeout ctx

                return!
                    agentLoop
                        { ctx with
                            State = state
                            HeartbeatTimer = heartbeatTimer }
            | RaftRPC rpcMsg ->
                let state, electionTimer, heartbeatTimer = receiveRaftRPC ctx rpcMsg

                let ctx2 =
                    { ctx with
                        State = state
                        ElectionTimer = electionTimer
                        HeartbeatTimer = heartbeatTimer }

                let ctx3 =
                    if state.Config <> ctx.Config then
                        { ctx2 with Config = state.Config }
                    else
                        ctx2

                return! agentLoop ctx3
            | GetState ch ->
                ch.Reply ctx.State
                return! agentLoop ctx
            | ClientCommand _
            | AddPeer _
            | RemovePeer _ as localMsg ->
                let state = handleLocalMessage ctx localMsg

                let ctx2 =
                    if state.Config <> ctx.Config then
                        { ctx with
                            State = state
                            Config = state.Config }
                    else
                        { ctx with State = state }

                return! agentLoop ctx2
            | TakeSnapshot(data, ch) ->
                let lastApplied = ctx.State.Volatile.LastApplied
                let lastTerm = Log.termAt lastApplied ctx.State.Persistent.Log
                let state = State.takeSnapshot lastApplied lastTerm data ctx.State
                saveIfChanged ctx state
                ch.Reply()
                return! agentLoop { ctx with State = state }
            | Shutdown ch ->
                NodeTimer.disposeTimer ctx.ElectionTimer
                NodeTimer.disposeTimer ctx.HeartbeatTimer
                ctx.CancellationTokenSource.Cancel()
                ch.Reply()
        }
