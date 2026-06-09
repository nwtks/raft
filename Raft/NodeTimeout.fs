namespace Raft

module NodeTimeout =
    let receiveElectionTimeout ctx =
        if ctx.State.Role = Leader then
            ctx.State, Keep
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

            finalState, Reset

    let handleElectionTimeout ctx =
        let state, electionAction = receiveElectionTimeout ctx
        let remainingReads = NodeRead.processPendingReads ctx.PendingReads state

        { State = state
          ElectionAction = electionAction
          HeartbeatAction = Keep
          PendingReads = remainingReads }

    let receiveHeartbeatTimeout ctx =
        if ctx.State.Role = Leader then
            NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport ctx.State
            let state = NodePromotion.tryPromoteNonVotingPeers ctx ctx.State
            state, Reset
        else
            ctx.State, Keep

    let handleHeartbeatTimeout ctx =
        let state, heartbeatAction = receiveHeartbeatTimeout ctx
        let remainingReads = NodeRead.processPendingReads ctx.PendingReads state

        { State = state
          ElectionAction = Keep
          HeartbeatAction = heartbeatAction
          PendingReads = remainingReads }
