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

    let handleRequestVote rv state =
        let state2 = State.updateTerm rv.CandidateTerm state

        let canVote =
            rv.CandidateTerm >= state2.Persistent.CurrentTerm
            && (state2.Persistent.VotedFor = None
                || state2.Persistent.VotedFor = Some rv.CandidateId)

        let logUpToDate =
            let myLastTerm = Log.lastTerm state2.Persistent.Log
            let myLastIndex = Log.lastIndex state2.Persistent.Log

            rv.LastLogTerm > myLastTerm
            || rv.LastLogTerm = myLastTerm && rv.LastLogIndex >= myLastIndex

        let grant = canVote && logUpToDate

        let newState =
            if grant then
                State.recordVote rv.CandidateId state2
            else
                state2

        let response =
            { VoterId = newState.Config.NodeId
              VoterTerm = newState.Persistent.CurrentTerm
              VoteGranted = grant }

        newState, response

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
