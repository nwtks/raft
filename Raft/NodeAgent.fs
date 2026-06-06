namespace Raft

module NodeAgent =
    [<TailCall>]
    let rec loopApplyCommitted onApply state lastApplied =
        if lastApplied < state.Volatile.CommitIndex then
            let next = lastApplied + 1L
            Log.getEntry next state.Persistent.Log |> Option.iter onApply
            loopApplyCommitted onApply state next
        else
            lastApplied

    let applyCommitted onApply state =
        let lastApplied = loopApplyCommitted onApply state state.Volatile.LastApplied
        State.updateLastApplied lastApplied state

    let saveIfChanged ctx newState =
        if ctx.State.Persistent <> newState.Persistent then
            ctx.Persistence.Save newState.Persistent

    let receiveElectionTimeout ctx =
        if ctx.State.Role <> Leader then
            let newState = Election.startElection ctx.State
            saveIfChanged ctx newState
            NodeBroadcaster.broadcastRequestVote ctx.Config ctx.Transport newState

            let finalState =
                if Set.count newState.VotesReceived >= State.quorumSize newState then
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

    let handleRaftMessage ctx rpcMsg =
        match rpcMsg with
        | RequestVoteMsg requestVote ->
            let state, response = Election.handleRequestVote requestVote ctx.State
            saveIfChanged ctx state
            let peer = ctx.Config.Peers |> List.find (fun p -> p.Id = requestVote.CandidateId)
            ctx.Transport.SendMessage peer (RequestVoteResponseMsg response) |> ignore
            state, true
        | RequestVoteResponseMsg response ->
            let state = Election.handleVoteResponse response.VoterId response ctx.State
            saveIfChanged ctx state
            state, false
        | AppendEntriesMsg appendEntries ->
            let state, response = Replication.handleAppendEntries appendEntries ctx.State
            saveIfChanged ctx state
            let peer = ctx.Config.Peers |> List.find (fun p -> p.Id = appendEntries.LeaderId)
            ctx.Transport.SendMessage peer (AppendEntriesResponseMsg response) |> ignore
            state, true
        | AppendEntriesResponseMsg response ->
            let state = Replication.handleAppendEntriesResponse response ctx.State
            let state2 = Replication.advanceCommitIndex state
            saveIfChanged ctx state2
            state2, false
        | ClientCommand(cmd, replyChannel) ->
            if ctx.State.Role = Leader then
                let state = Replication.appendCommand cmd ctx.State
                saveIfChanged ctx state
                NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport state
                replyChannel |> Option.iter (fun ch -> ch.Reply true)
                state, false
            else
                replyChannel |> Option.iter (fun ch -> ch.Reply false)
                ctx.State, false

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

        applyCommitted ctx.OnApply newState, electionTimer, heartbeatTimer

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

                return!
                    agentLoop
                        { ctx with
                            State = state
                            ElectionTimer = electionTimer
                            HeartbeatTimer = heartbeatTimer }
            | GetState ch ->
                ch.Reply ctx.State
                return! agentLoop ctx
            | Shutdown ch ->
                NodeTimer.disposeTimer ctx.ElectionTimer
                NodeTimer.disposeTimer ctx.HeartbeatTimer
                ctx.CancellationTokenSource.Cancel()
                ch.Reply()
        }
