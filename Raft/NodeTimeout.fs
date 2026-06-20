namespace Raft

module NodeTimeout =
    let handleTimeoutWith receiveFn isElection ctx =
        let state, action = receiveFn ctx
        let remainingReads = NodeRead.processPendingReads ctx.PendingReads state

        { State = state
          ElectionAction = if isElection then action else Keep
          HeartbeatAction = if isElection then Keep else action
          PendingReads = remainingReads }

    let tryBecomeLeaderInSingleNodeCluster ctx state =
        if List.isEmpty ctx.Config.Peers && State.hasQuorum state.VotesReceived state then
            let newState = State.initLeaderState state
            NodeUtil.saveIfChanged ctx newState
            NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport newState
            newState
        else
            state

    let receiveElectionTimeout ctx =
        if ctx.State.Role = Leader then
            ctx.State, Keep
        else
            let state = Election.startElection ctx.State
            NodeUtil.saveIfChanged ctx state
            NodeBroadcaster.broadcastRequestVote ctx.Config ctx.Transport state
            let finalState = tryBecomeLeaderInSingleNodeCluster ctx state
            finalState, Reset

    let handleElectionTimeout ctx =
        handleTimeoutWith receiveElectionTimeout true ctx

    let receiveHeartbeatTimeout ctx =
        if ctx.State.Role = Leader then
            NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport ctx.State
            let state = NodePromotion.tryPromoteNonVotingPeers ctx ctx.State
            state, Reset
        else
            ctx.State, Keep

    let handleHeartbeatTimeout ctx =
        handleTimeoutWith receiveHeartbeatTimeout false ctx
