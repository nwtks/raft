namespace Raft

module Election =
    let startElection state =
        let newTerm = state.Persistent.CurrentTerm + 1L

        { state with
            Role = Candidate
            Persistent =
                { state.Persistent with
                    CurrentTerm = newTerm
                    VotedFor = Some state.Config.NodeId }
            VotesReceived = Set.singleton state.Config.NodeId
            LeaderState = None
            CurrentLeader = None }

    let createRequestVote state =
        { CandidateTerm = state.Persistent.CurrentTerm
          CandidateId = state.Config.NodeId
          LastLogIndex = Log.lastIndex state.Persistent.Log
          LastLogTerm = Log.lastTerm state.Persistent.Log }

    let canVoteFor candidateId state =
        state.Persistent.VotedFor = None || state.Persistent.VotedFor = Some candidateId

    let isLogUpToDate candidateLastTerm candidateLastIndex state =
        let myLastTerm = Log.lastTerm state.Persistent.Log
        let myLastIndex = Log.lastIndex state.Persistent.Log

        candidateLastTerm > myLastTerm
        || candidateLastTerm = myLastTerm && candidateLastIndex >= myLastIndex

    let handleRequestVote rv state =
        let state2 = State.updateTerm rv.CandidateTerm state

        let grant =
            rv.CandidateTerm >= state2.Persistent.CurrentTerm
            && canVoteFor rv.CandidateId state2
            && isLogUpToDate rv.LastLogTerm rv.LastLogIndex state2

        let newState =
            if grant then
                State.recordVote rv.CandidateId state2
            else
                state2

        newState,
        { VoterId = newState.Config.NodeId
          VoterTerm = newState.Persistent.CurrentTerm
          VoteGranted = grant }

    let handleVoteResponse fromNode resp state =
        if resp.VoterTerm > state.Persistent.CurrentTerm then
            State.updateTerm resp.VoterTerm state
        elif state.Role = Candidate && resp.VoteGranted then
            let newState = State.addVoteReceived fromNode state

            if State.hasQuorum newState.VotesReceived newState then
                State.initLeaderState newState
            else
                newState
        else
            state
