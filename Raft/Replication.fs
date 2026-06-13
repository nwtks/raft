namespace Raft

module Replication =
    let createEntries entries followerId state =
        match state.LeaderState with
        | None -> None
        | Some ls ->
            let nextIndex =
                ls.NextIndex |> Map.tryFind followerId |> Option.defaultValue Log.firstLogIndex

            let prevLogIndex = nextIndex - 1L

            match state.Persistent.Snapshot with
            | Some snap when prevLogIndex < snap.LastIncludedIndex -> None
            | _ ->
                let prevLogTerm = Log.termAt prevLogIndex state.Persistent.Log

                Some
                    { LeaderTerm = state.Persistent.CurrentTerm
                      LeaderId = state.Config.NodeId
                      PrevLogIndex = prevLogIndex
                      PrevLogTerm = prevLogTerm
                      Entries = entries nextIndex
                      LeaderCommit = state.Volatile.CommitIndex }

    let createAppendEntries followerId state =
        createEntries (fun index -> Log.entriesFrom index state.Persistent.Log) followerId state

    let createHeartbeat followerId state =
        createEntries (fun _ -> []) followerId state

    let isLogConsistent prevLogIndex prevLogTerm log =
        prevLogIndex = Log.initialLogIndex
        || Log.termAt prevLogIndex log = prevLogTerm && prevLogIndex <= Log.lastIndex log

    [<TailCall>]
    let rec findFirstIdx idx term log =
        if idx > Log.firstLogIndex && Log.termAt (idx - 1L) log = term then
            findFirstIdx (idx - 1L) term log
        else
            idx

    let calculateConflictInfo ae log =
        if ae.PrevLogIndex > Log.lastIndex log then
            Log.initialTerm, Log.lastIndex log + 1L
        else
            let term = Log.termAt ae.PrevLogIndex log
            let firstIdx = findFirstIdx ae.PrevLogIndex term log
            term, firstIdx

    let acceptAppendEntries ae state =
        let newLog = Log.mergeEntries ae.Entries state.Persistent.Log

        let newCommitIndex =
            if ae.LeaderCommit > state.Volatile.CommitIndex then
                let lastNewIndex =
                    match ae.Entries with
                    | [] -> Log.lastIndex newLog
                    | _ -> (List.last ae.Entries).Index

                min ae.LeaderCommit lastNewIndex
            else
                state.Volatile.CommitIndex

        let newState = State.updateLogAndCommit newLog newCommitIndex state
        let matchIdx = Log.lastIndex newLog

        newState,
        { FollowerTerm = newState.Persistent.CurrentTerm
          Success = true
          MatchIndex = matchIdx
          FollowerId = newState.Config.NodeId
          ConflictTerm = Log.initialTerm
          ConflictIndex = Log.initialLogIndex }

    let handleAppendEntries (ae: AppendEntries) state =
        if ae.LeaderTerm < state.Persistent.CurrentTerm then
            state,
            { FollowerTerm = state.Persistent.CurrentTerm
              Success = false
              MatchIndex = Log.initialLogIndex
              FollowerId = state.Config.NodeId
              ConflictTerm = Log.initialTerm
              ConflictIndex = Log.initialLogIndex }
        else
            let newState = State.followLeader ae.LeaderTerm ae.LeaderId state

            if isLogConsistent ae.PrevLogIndex ae.PrevLogTerm newState.Persistent.Log then
                acceptAppendEntries ae newState
            else
                let conflictTerm, conflictIndex = calculateConflictInfo ae newState.Persistent.Log

                newState,
                { FollowerTerm = newState.Persistent.CurrentTerm
                  Success = false
                  MatchIndex = Log.initialLogIndex
                  FollowerId = newState.Config.NodeId
                  ConflictTerm = conflictTerm
                  ConflictIndex = conflictIndex }

    let updateMatchIndices (resp: AppendEntriesResponse) leaderState =
        let newMatchIndex =
            leaderState.MatchIndex |> Map.add resp.FollowerId resp.MatchIndex

        let newNextIndex =
            leaderState.NextIndex |> Map.add resp.FollowerId (resp.MatchIndex + 1L)

        newNextIndex, newMatchIndex

    let calculateBackoffNextIndex (resp: AppendEntriesResponse) log =
        if resp.ConflictTerm = Log.initialTerm then
            max Log.firstLogIndex resp.ConflictIndex
        else
            match Log.lastIndexOfTerm resp.ConflictTerm log with
            | None -> max Log.firstLogIndex resp.ConflictIndex
            | Some lastIdx -> max Log.firstLogIndex lastIdx

    let onSameTermSuccess (resp: AppendEntriesResponse) leaderState state =
        let newNextIndex, newMatchIndex = updateMatchIndices resp leaderState
        State.updateLeaderState newNextIndex newMatchIndex state

    let onSameTermFailure (resp: AppendEntriesResponse) leaderState state =
        let newNext = calculateBackoffNextIndex resp state.Persistent.Log
        let newNextIndex = leaderState.NextIndex |> Map.add resp.FollowerId newNext
        State.updateLeaderState newNextIndex leaderState.MatchIndex state

    let handleAppendEntriesResponse (resp: AppendEntriesResponse) state =
        if resp.FollowerTerm > state.Persistent.CurrentTerm then
            State.updateTerm resp.FollowerTerm state
        elif resp.FollowerTerm < state.Persistent.CurrentTerm then
            state
        else
            match state.LeaderState with
            | None -> state
            | Some ls ->
                if resp.Success then
                    onSameTermSuccess resp ls state
                else
                    onSameTermFailure resp ls state

    let createInstallSnapshot followerId state =
        match state.LeaderState, state.Persistent.Snapshot with
        | Some ls, Some snap ->
            let nextIdx =
                ls.NextIndex |> Map.tryFind followerId |> Option.defaultValue Log.firstLogIndex

            if nextIdx <= snap.LastIncludedIndex + 1L then
                Some
                    { LeaderTerm = state.Persistent.CurrentTerm
                      LeaderId = state.Config.NodeId
                      LastIncludedIndex = snap.LastIncludedIndex
                      LastIncludedTerm = snap.LastIncludedTerm
                      Data = snap.StateMachineData }
            else
                None
        | _ -> None

    let handleInstallSnapshot snap state =
        if snap.LeaderTerm < state.Persistent.CurrentTerm then
            state,
            { FollowerTerm = state.Persistent.CurrentTerm
              FollowerId = state.Config.NodeId
              Success = false
              LastIncludedIndex = Log.initialLogIndex }
        else
            let newState =
                State.followLeader snap.LeaderTerm snap.LeaderId state
                |> State.takeSnapshot snap.LastIncludedIndex snap.LastIncludedTerm snap.Data

            newState,
            { FollowerTerm = newState.Persistent.CurrentTerm
              FollowerId = state.Config.NodeId
              Success = true
              LastIncludedIndex = snap.LastIncludedIndex }

    let handleInstallSnapshotResponse resp state =
        if resp.FollowerTerm > state.Persistent.CurrentTerm then
            State.updateTerm resp.FollowerTerm state
        else
            match state.LeaderState with
            | Some ls ->
                if resp.Success then
                    let newMatchIndex = ls.MatchIndex |> Map.add resp.FollowerId resp.LastIncludedIndex

                    let newNextIndex =
                        ls.NextIndex |> Map.add resp.FollowerId (resp.LastIncludedIndex + 1L)

                    State.updateLeaderState newNextIndex newMatchIndex state
                else
                    state
            | None -> state

    let canCommitIndex state leaderState index =
        let supporters =
            leaderState.MatchIndex
            |> Map.toSeq
            |> Seq.filter (fun (_, m) -> m >= index)
            |> Seq.map fst
            |> Set.ofSeq
            |> Set.add state.Config.NodeId

        State.hasQuorum supporters state
        && index > state.Volatile.CommitIndex
        && Log.termAt index state.Persistent.Log = state.Persistent.CurrentTerm

    let advanceCommitIndex state =
        match state.LeaderState with
        | Some ls ->
            let lastIdx = Log.lastIndex state.Persistent.Log

            let newCommitIndex =
                seq { lastIdx .. -1L .. state.Volatile.CommitIndex + 1L }
                |> Seq.tryFind (canCommitIndex state ls)
                |> Option.defaultValue state.Volatile.CommitIndex

            State.updateCommitIndex newCommitIndex state
        | None -> state

    let appendJointConsensus oldPeers newPeers state =
        if state.Role = Leader then
            let command =
                ConfigChange.ConfigCommandPrefix
                + ConfigChange.serialize (JointChange(oldPeers, newPeers))

            let newLog = Log.append state.Persistent.CurrentTerm command state.Persistent.Log
            State.updateLog newLog state
        else
            state

    let appendFinalConfiguration peers state =
        if state.Role = Leader then
            let command =
                ConfigChange.ConfigCommandPrefix + ConfigChange.serialize (FinalChange peers)

            let newLog = Log.append state.Persistent.CurrentTerm command state.Persistent.Log
            State.updateLog newLog state
        else
            state

    let appendCommand command state =
        if state.Role = Leader then
            let newLog = Log.append state.Persistent.CurrentTerm command state.Persistent.Log
            State.updateLog newLog state
        else
            state

    let appendCommandWithSession command clientId seqNum state =
        if state.Role = Leader then
            let newLog =
                Log.appendWithSession state.Persistent.CurrentTerm command clientId seqNum state.Persistent.Log

            State.updateLog newLog state
        else
            state
