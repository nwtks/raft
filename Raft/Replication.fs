namespace Raft

module Replication =
    let createEntries entries followerId state =
        match state.LeaderState with
        | None -> None
        | Some ls ->
            let nextIdx = ls.NextIndex |> Map.tryFind followerId |> Option.defaultValue 1L
            let prevLogIndex = nextIdx - 1L

            match state.Persistent.Snapshot with
            | Some snap when prevLogIndex < snap.LastIncludedIndex -> None
            | _ ->
                let prevLogTerm = Log.termAt prevLogIndex state.Persistent.Log

                Some
                    { LeaderTerm = state.Persistent.CurrentTerm
                      LeaderId = state.Config.NodeId
                      PrevLogIndex = prevLogIndex
                      PrevLogTerm = prevLogTerm
                      Entries = entries nextIdx
                      LeaderCommit = state.Volatile.CommitIndex }

    let createInstallSnapshot followerId state =
        match state.LeaderState, state.Persistent.Snapshot with
        | Some ls, Some snap ->
            let nextIdx = ls.NextIndex |> Map.tryFind followerId |> Option.defaultValue 1L

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

    let createAppendEntries followerId state =
        createEntries (fun index -> Log.entriesFrom index state.Persistent.Log) followerId state

    let createHeartbeat followerId state =
        createEntries (fun _ -> []) followerId state

    let handleAppendEntries (ae: AppendEntries) state =
        if ae.LeaderTerm < state.Persistent.CurrentTerm then
            let response =
                { FollowerTerm = state.Persistent.CurrentTerm
                  Success = false
                  MatchIndex = 0L
                  FollowerId = state.Config.NodeId
                  ConflictTerm = 0L
                  ConflictIndex = 0L }

            state, response
        else
            let state2 = State.followLeader ae.LeaderTerm ae.LeaderId state

            let logOk =
                ae.PrevLogIndex = 0L
                || Log.termAt ae.PrevLogIndex state2.Persistent.Log = ae.PrevLogTerm
                   && ae.PrevLogIndex <= Log.lastIndex state2.Persistent.Log

            if logOk then
                let newLog = Log.mergeEntries ae.Entries state2.Persistent.Log

                let newCommitIndex =
                    if ae.LeaderCommit > state2.Volatile.CommitIndex then
                        let lastNewIndex =
                            match ae.Entries with
                            | [] -> Log.lastIndex newLog
                            | _ -> (List.last ae.Entries).Index

                        min ae.LeaderCommit lastNewIndex
                    else
                        state2.Volatile.CommitIndex

                let newState = State.updateLogAndCommit newLog newCommitIndex state2
                let matchIdx = Log.lastIndex newLog

                let response =
                    { FollowerTerm = newState.Persistent.CurrentTerm
                      Success = true
                      MatchIndex = matchIdx
                      FollowerId = newState.Config.NodeId
                      ConflictTerm = 0L
                      ConflictIndex = 0L }

                newState, response
            else
                let conflictTerm, conflictIndex =
                    if ae.PrevLogIndex > Log.lastIndex state2.Persistent.Log then
                        0L, Log.lastIndex state2.Persistent.Log + 1L
                    else
                        let term = Log.termAt ae.PrevLogIndex state2.Persistent.Log

                        let firstIdx =
                            let mutable idx = ae.PrevLogIndex

                            while idx > 1L && Log.termAt (idx - 1L) state2.Persistent.Log = term do
                                idx <- idx - 1L

                            idx

                        term, firstIdx

                let response =
                    { FollowerTerm = state2.Persistent.CurrentTerm
                      Success = false
                      MatchIndex = 0L
                      FollowerId = state2.Config.NodeId
                      ConflictTerm = conflictTerm
                      ConflictIndex = conflictIndex }

                state2, response

    let handleInstallSnapshot snap state =
        if snap.LeaderTerm < state.Persistent.CurrentTerm then
            state,
            { FollowerTerm = state.Persistent.CurrentTerm
              FollowerId = state.Config.NodeId
              Success = false
              LastIncludedIndex = 0L }
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
            | None -> state
            | Some ls ->
                if resp.Success then
                    let newMatchIndex = ls.MatchIndex |> Map.add resp.FollowerId resp.LastIncludedIndex

                    let newNextIndex =
                        ls.NextIndex |> Map.add resp.FollowerId (resp.LastIncludedIndex + 1L)

                    State.updateLeaderState newNextIndex newMatchIndex state
                else
                    state

    let handleAppendEntriesResponse (resp: AppendEntriesResponse) state =
        if resp.FollowerTerm > state.Persistent.CurrentTerm then
            State.updateTerm resp.FollowerTerm state
        else
            match state.LeaderState with
            | None -> state
            | Some ls ->
                if resp.Success then
                    let newMatchIndex = ls.MatchIndex |> Map.add resp.FollowerId resp.MatchIndex
                    let newNextIndex = ls.NextIndex |> Map.add resp.FollowerId (resp.MatchIndex + 1L)
                    State.updateLeaderState newNextIndex newMatchIndex state
                else
                    let newNext =
                        if resp.ConflictTerm = 0L then
                            max 1L resp.ConflictIndex
                        else
                            match Log.lastIndexOfTerm resp.ConflictTerm state.Persistent.Log with
                            | None -> max 1L resp.ConflictIndex
                            | Some lastIdx -> max 1L lastIdx

                    let newNextIndex = ls.NextIndex |> Map.add resp.FollowerId newNext
                    State.updateLeaderState newNextIndex ls.MatchIndex state

    let canCommitIndex matchIndices majority state index =
        let count = matchIndices |> List.filter (fun m -> m >= index) |> List.length

        count >= majority
        && index > state.Volatile.CommitIndex
        && Log.termAt index state.Persistent.Log = state.Persistent.CurrentTerm

    let advanceCommitIndex state =
        match state.LeaderState with
        | None -> state
        | Some ls ->
            let matchIndices =
                ls.MatchIndex
                |> Map.values
                |> Seq.toList
                |> List.append [ Log.lastIndex state.Persistent.Log ]
                |> List.sortDescending

            let majority = State.quorumSize state
            let lastIdx = Log.lastIndex state.Persistent.Log

            let newCommitIndex =
                seq { lastIdx .. -1L .. state.Volatile.CommitIndex + 1L }
                |> Seq.tryFind (canCommitIndex matchIndices majority state)
                |> Option.defaultValue state.Volatile.CommitIndex

            State.updateCommitIndex newCommitIndex state

    let appendConfiguration peers state =
        if state.Role <> Leader then
            state
        else
            let json = System.Text.Json.JsonSerializer.Serialize(peers)
            let command = Constants.ConfigCommandPrefix + json
            let newLog = Log.append state.Persistent.CurrentTerm command state.Persistent.Log
            State.updateLog newLog state

    let appendCommand command state =
        if state.Role <> Leader then
            state
        else
            let newLog = Log.append state.Persistent.CurrentTerm command state.Persistent.Log
            State.updateLog newLog state
