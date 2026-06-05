namespace Raft

module NodeAgent =
    let applyCommitted onApply state =
        let mutable lastApplied = state.Volatile.LastApplied

        while lastApplied < state.Volatile.CommitIndex do
            lastApplied <- lastApplied + 1L

            match Log.getEntry lastApplied state.Persistent.Log with
            | Some entry -> onApply entry
            | None -> ()

        State.updateLastApplied lastApplied state

    let saveIfChanged ctx newState =
        if ctx.State.Persistent <> newState.Persistent then
            ctx.Persistence.Save newState.Persistent

    let receiveElectionTimeout ctx =
        if ctx.State.Role <> Leader then
            let newState = Election.startElection ctx.State
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
            ctx.Transport.SendMessage peer (RequestVoteResponseMsg response)
            state, true
        | RequestVoteResponseMsg response ->
            let state = Election.handleVoteResponse response.VoterId response ctx.State
            saveIfChanged ctx state
            state, false
        | AppendEntriesMsg appendEntries ->
            let state, response = Replication.handleAppendEntries appendEntries ctx.State
            saveIfChanged ctx state
            let peer = ctx.Config.Peers |> List.find (fun p -> p.Id = appendEntries.LeaderId)
            ctx.Transport.SendMessage peer (AppendEntriesResponseMsg response)
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

        if oldRole <> Leader && newState.Role = Leader then
            NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport newState
            let heartbeatTimer = NodeTimer.resetHeartbeatTimer ctx
            NodeTimer.stopTimer ctx.ElectionTimer
            let state = applyCommitted ctx.OnApply newState
            state, ctx.ElectionTimer, heartbeatTimer
        elif oldRole = Leader && newState.Role <> Leader then
            NodeTimer.stopTimer ctx.HeartbeatTimer
            let electionTimer = NodeTimer.resetElectionTimer ctx
            let state = applyCommitted ctx.OnApply newState
            state, electionTimer, ctx.HeartbeatTimer
        elif sendReply then
            let electionTimer = NodeTimer.resetElectionTimer ctx
            let state = applyCommitted ctx.OnApply newState
            state, electionTimer, ctx.HeartbeatTimer
        else
            let state = applyCommitted ctx.OnApply newState
            state, ctx.ElectionTimer, ctx.HeartbeatTimer

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
        }
