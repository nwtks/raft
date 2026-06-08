namespace Raft

module NodeTimeout =
    let receiveElectionTimeout ctx =
        if ctx.State.Role = Leader then
            ctx.State, ctx.ElectionTimer
        else
            let state = Election.startElection ctx.State
            NodeUtil.saveIfChanged ctx state
            NodeBroadcaster.broadcastRequestVote ctx.Config ctx.Transport state

            let finalState =
                if List.isEmpty ctx.Config.Peers && State.hasQuorum state.VotesReceived state then
                    let newState = State.initLeaderState state
                    NodeUtil.saveIfChanged ctx newState
                    NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport newState
                    newState
                else
                    state

            finalState, NodeTimer.resetElectionTimer ctx

    let receiveHeartbeatTimeout ctx =
        if ctx.State.Role = Leader then
            NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport ctx.State
            let state = NodePromotion.tryPromoteNonVotingPeers ctx ctx.State
            state, NodeTimer.resetHeartbeatTimer ctx
        else
            ctx.State, ctx.HeartbeatTimer
