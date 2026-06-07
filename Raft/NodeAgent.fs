namespace Raft

module NodeAgent =
    let log msg = printfn "[Node] %s" msg

    [<TailCall>]
    let rec loopApplyCommitted onApply state lastApplied =
        if lastApplied < state.Volatile.CommitIndex then
            let next = lastApplied + 1L

            match Log.getEntry next state.Persistent.Log with
            | Some entry when entry.Command.StartsWith ConfigChange.ConfigCommandPrefix ->
                let stateWithSession =
                    match entry.ClientId, entry.SeqNum with
                    | Some cId, Some sNum when
                        state.Persistent.SessionTable |> Map.tryFind cId |> Option.defaultValue -1L < sNum
                        ->
                        State.updateSessionTable cId sNum state
                    | _ -> state

                match ConfigChange.parse entry.Command with
                | Some(JointChange(oldPeers, newPeers)) ->
                    let state2 = State.enterJointConsensus oldPeers newPeers stateWithSession
                    loopApplyCommitted onApply state2 next
                | Some(FinalChange newPeers) ->
                    let state2 = State.exitJointConsensus newPeers stateWithSession
                    loopApplyCommitted onApply state2 next
                | None ->
                    let json = entry.Command.Substring ConfigChange.ConfigCommandPrefix.Length
                    let newPeers = System.Text.Json.JsonSerializer.Deserialize<PeerInfo list> json
                    let state2 = State.updateConfig newPeers stateWithSession
                    loopApplyCommitted onApply state2 next
            | Some entry when entry.Command = "" -> loopApplyCommitted onApply state next
            | Some entry ->
                let isDuplicate =
                    match entry.ClientId, entry.SeqNum with
                    | Some cId, Some sNum ->
                        state.Persistent.SessionTable |> Map.tryFind cId |> Option.defaultValue -1L
                        >= sNum
                    | _ -> false

                let state2 =
                    if isDuplicate then
                        state
                    elif entry.ClientId.IsSome && entry.SeqNum.IsSome then
                        State.updateSessionTable entry.ClientId.Value entry.SeqNum.Value state
                    else
                        state

                if not isDuplicate then
                    onApply entry

                loopApplyCommitted onApply state2 next
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

    let processPendingReads pendingReads state =
        pendingReads
        |> List.filter (fun pr ->
            let canServe =
                state.Role = Leader
                && state.Volatile.CommitIndex > 0L
                && Log.termAt state.Volatile.CommitIndex state.Persistent.Log = state.Persistent.CurrentTerm
                && State.hasQuorum pr.Responses state

            if state.Role <> Leader then
                let leaderInfo =
                    state.CurrentLeader
                    |> Option.bind (fun leaderId -> state.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))

                pr.ReplyChannel.Reply(ReadRedirect leaderInfo)
                false
            elif canServe && state.Volatile.LastApplied >= pr.ReadIndex then
                pr.ReplyChannel.Reply ReadReady
                false
            else
                true)

    let autoSnapshotIfNeeded ctx state =
        if ctx.Config.SnapshotAutoThreshold > 0 then
            let lastSnapIndex =
                match state.Persistent.Snapshot with
                | Some snap -> snap.LastIncludedIndex
                | None -> 0L

            let logEntryCount = Log.lastIndex state.Persistent.Log - lastSnapIndex

            if logEntryCount >= int64 ctx.Config.SnapshotAutoThreshold then
                let lastApplied = state.Volatile.LastApplied
                let lastTerm = Log.termAt lastApplied state.Persistent.Log
                let data = ctx.OnGetSnapshotData()
                let newState = State.takeSnapshot lastApplied lastTerm data state
                saveIfChanged ctx newState
                newState
            else
                state
        else
            state

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

    let tryPromoteNonVotingPeers ctx state =
        if state.Role = Leader && not (List.isEmpty state.NonVotingPeers) then
            let lastIndex = Log.lastIndex state.Persistent.Log

            let readyPeers =
                state.NonVotingPeers
                |> List.filter (fun p ->
                    match state.LeaderState with
                    | Some ls -> ls.MatchIndex |> Map.tryFind p.Id |> Option.defaultValue -1L >= lastIndex
                    | None -> false)

            match readyPeers with
            | [] -> state
            | readyPeers ->
                let caughtUpIds = readyPeers |> List.map (fun p -> p.Id) |> Set.ofList

                let remainingNonVoting =
                    state.NonVotingPeers |> List.filter (fun p -> not (caughtUpIds.Contains p.Id))

                let stateWithRemaining =
                    { state with
                        NonVotingPeers = remainingNonVoting }

                let oldPeers = state.Config.Peers
                let allVoting = List.append oldPeers readyPeers
                let state2 = Replication.appendJointConsensus oldPeers allVoting stateWithRemaining
                saveIfChanged ctx state2
                NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state2
                let appliedState = applyCommitted ctx.OnApply state2
                tryFinalizeConfiguration (autoSnapshotIfNeeded ctx appliedState)
        else
            state

    let receiveElectionTimeout ctx =
        if ctx.State.Role <> Leader then
            let newState = Election.startElection ctx.State
            saveIfChanged ctx newState
            NodeBroadcaster.broadcastRequestVote ctx.Config ctx.Transport newState

            let finalState =
                if State.hasQuorum newState.VotesReceived newState then
                    let state = State.initLeaderState newState
                    saveIfChanged ctx state
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
            let state = tryPromoteNonVotingPeers ctx ctx.State
            state, NodeTimer.resetHeartbeatTimer ctx
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
            let state =
                Replication.handleAppendEntriesResponse response ctx.State
                |> Replication.advanceCommitIndex
                |> tryPromoteNonVotingPeers ctx

            saveIfChanged ctx state
            state, false
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

    let handleLocalMessage ctx =
        function
        | ClientCommand(cmd, clientId, seqNum, replyChannel) ->
            if ctx.State.Role = Leader then
                match clientId, seqNum with
                | Some cId, Some sNum ->
                    match ctx.State.Persistent.SessionTable |> Map.tryFind cId with
                    | Some lastSeq when sNum <= lastSeq ->
                        replyChannel.Reply Accepted
                        ctx.State
                    | _ ->
                        let state = Replication.appendCommandWithSession cmd cId sNum ctx.State
                        saveIfChanged ctx state
                        NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
                        let appliedState = applyCommitted ctx.OnApply state
                        replyChannel.Reply Accepted
                        tryFinalizeConfiguration (autoSnapshotIfNeeded ctx appliedState)
                | _ ->
                    let state = Replication.appendCommand cmd ctx.State
                    saveIfChanged ctx state
                    NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
                    let appliedState = applyCommitted ctx.OnApply state
                    replyChannel.Reply Accepted
                    tryFinalizeConfiguration (autoSnapshotIfNeeded ctx appliedState)
            else
                let leaderInfo =
                    ctx.State.CurrentLeader
                    |> Option.bind (fun leaderId -> ctx.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))

                replyChannel.Reply(Redirect leaderInfo)
                ctx.State
        | AddPeer(peerInfo, replyChannel) ->
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

                    let state2 =
                        match state.LeaderState with
                        | Some ls ->
                            { state with
                                LeaderState =
                                    Some
                                        { ls with
                                            NextIndex = ls.NextIndex |> Map.add peerInfo.Id (lastIdx + 1L)
                                            MatchIndex = ls.MatchIndex |> Map.add peerInfo.Id 0L } }
                        | None -> state

                    NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state2
                    replyChannel.Reply true
                    state2
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
                let appliedState = applyCommitted ctx.OnApply state
                replyChannel.Reply true
                tryFinalizeConfiguration (autoSnapshotIfNeeded ctx appliedState)
            else
                replyChannel.Reply false
                ctx.State
        | _ ->
            log $"Warning: unexpected message routed to handleLocalMessage, ignoring"
            ctx.State

    let receiveRaftRPC ctx rpcMsg =
        let oldRole = ctx.State.Role
        let newState, sendReply = handleRaftMessage ctx rpcMsg

        let finalState =
            applyCommitted ctx.OnApply newState
            |> autoSnapshotIfNeeded ctx
            |> tryFinalizeConfiguration

        let electionTimer, heartbeatTimer =
            if oldRole <> Leader && finalState.Role = Leader then
                NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport finalState
                NodeTimer.stopTimer ctx.ElectionTimer
                ctx.ElectionTimer, NodeTimer.resetHeartbeatTimer ctx
            elif oldRole = Leader && finalState.Role <> Leader then
                NodeTimer.stopTimer ctx.HeartbeatTimer
                NodeTimer.resetElectionTimer ctx, ctx.HeartbeatTimer
            elif sendReply then
                NodeTimer.resetElectionTimer ctx, ctx.HeartbeatTimer
            else
                ctx.ElectionTimer, ctx.HeartbeatTimer

        finalState, electionTimer, heartbeatTimer

    [<TailCall>]
    let rec agentLoop ctx =
        async {
            let! msg = ctx.Inbox.Receive()

            match msg with
            | ElectionTimeout ->
                let state, electionTimer = receiveElectionTimeout ctx
                let remainingReads = processPendingReads ctx.PendingReads state

                return!
                    agentLoop
                        { ctx with
                            State = state
                            ElectionTimer = electionTimer
                            PendingReads = remainingReads }
            | HeartbeatTimeout ->
                let state, heartbeatTimer = receiveHeartbeatTimeout ctx

                return!
                    agentLoop
                        { ctx with
                            State = state
                            HeartbeatTimer = heartbeatTimer }
            | RaftRPC rpcMsg ->
                let state, electionTimer, heartbeatTimer = receiveRaftRPC ctx rpcMsg

                let updatedPending =
                    match rpcMsg with
                    | AppendEntriesResponseMsg resp when
                        state.Role = Leader && resp.FollowerTerm <= state.Persistent.CurrentTerm
                        ->
                        ctx.PendingReads
                        |> List.map (fun pr ->
                            { pr with
                                Responses = Set.add resp.FollowerId pr.Responses })
                    | _ -> ctx.PendingReads

                let remainingReads = processPendingReads updatedPending state

                let ctx2 =
                    { ctx with
                        State = state
                        ElectionTimer = electionTimer
                        HeartbeatTimer = heartbeatTimer
                        PendingReads = remainingReads }

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
                let oldRole = ctx.State.Role
                let state = handleLocalMessage ctx localMsg
                let remainingReads = processPendingReads ctx.PendingReads state

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

                let ctx2 =
                    { ctx with
                        State = state
                        Config = state.Config
                        ElectionTimer = electionTimer
                        HeartbeatTimer = heartbeatTimer
                        PendingReads = remainingReads }

                return! agentLoop ctx2
            | LinearizableRead replyChannel ->
                let ctx2 =
                    if ctx.State.Role = Leader then
                        let readIndex = ctx.State.Volatile.CommitIndex
                        NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport ctx.State

                        let pr =
                            { ReadIndex = readIndex
                              ReplyChannel = replyChannel
                              Responses = Set.singleton ctx.State.Config.NodeId }

                        let remainingReads = processPendingReads (ctx.PendingReads @ [ pr ]) ctx.State

                        { ctx with
                            PendingReads = remainingReads }
                    else
                        let leaderInfo =
                            ctx.State.CurrentLeader
                            |> Option.bind (fun leaderId -> ctx.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))

                        replyChannel.Reply(ReadRedirect leaderInfo)
                        ctx

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
