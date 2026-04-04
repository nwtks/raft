namespace Raft

module Replication =
    let createEntries
        (entries: LogIndex -> LogEntry list)
        (followerId: NodeId)
        (state: RaftState)
        : AppendEntries option =
        match state.LeaderState with
        | None -> None
        | Some ls ->
            let nextIdx = ls.NextIndex |> Map.tryFind followerId |> Option.defaultValue 1L
            let prevLogIndex = nextIdx - 1L
            let prevLogTerm = Log.termAt prevLogIndex state.Persistent.Log

            Some
                { LeaderTerm = state.Persistent.CurrentTerm
                  LeaderId = state.Config.NodeId
                  PrevLogIndex = prevLogIndex
                  PrevLogTerm = prevLogTerm
                  Entries = entries nextIdx
                  LeaderCommit = state.Volatile.CommitIndex }

    let createAppendEntries (followerId: NodeId) (state: RaftState) : AppendEntries option =
        createEntries (fun index -> Log.entriesFrom index state.Persistent.Log) followerId state

    let createHeartbeat (followerId: NodeId) (state: RaftState) : AppendEntries option =
        createEntries (fun _ -> Log.empty) followerId state

    let handleAppendEntries (ae: AppendEntries) (state: RaftState) : RaftState * AppendEntriesResponse =
        if ae.LeaderTerm < state.Persistent.CurrentTerm then
            let response =
                { FollowerTerm = state.Persistent.CurrentTerm
                  Success = false
                  MatchIndex = 0L
                  FollowerId = state.Config.NodeId }

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
                      FollowerId = newState.Config.NodeId }

                newState, response
            else
                let response =
                    { FollowerTerm = state2.Persistent.CurrentTerm
                      Success = false
                      MatchIndex = 0L
                      FollowerId = state2.Config.NodeId }

                state2, response

    let handleAppendEntriesResponse (resp: AppendEntriesResponse) (state: RaftState) : RaftState =
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
                    let currentNext =
                        ls.NextIndex |> Map.tryFind resp.FollowerId |> Option.defaultValue 1L

                    let newNext = max 1L (currentNext - 1L)
                    let newNextIndex = ls.NextIndex |> Map.add resp.FollowerId newNext
                    State.updateLeaderState newNextIndex ls.MatchIndex state

    let canCommitIndex (matchIndices: LogIndex list) (majority: int) (state: RaftState) (index: LogIndex) =
        let count = matchIndices |> List.filter (fun m -> m >= index) |> List.length

        count >= majority
        && index > state.Volatile.CommitIndex
        && Log.termAt index state.Persistent.Log = state.Persistent.CurrentTerm

    let advanceCommitIndex (state: RaftState) : RaftState =
        match state.LeaderState with
        | None -> state
        | Some ls ->
            let matchIndices =
                ls.MatchIndex
                |> Map.values
                |> Seq.toList
                |> List.append [ Log.lastIndex state.Persistent.Log ] // include leader's own
                |> List.sortDescending

            let majority = State.quorumSize state

            let newCommitIndex =
                [ state.Volatile.CommitIndex .. Log.lastIndex state.Persistent.Log ]
                |> List.tryFindBack (canCommitIndex matchIndices majority state)
                |> Option.defaultValue state.Volatile.CommitIndex

            State.updateCommitIndex newCommitIndex state

    let appendCommand (command: string) (state: RaftState) : RaftState =
        if state.Role <> Leader then
            state
        else
            let newLog = Log.append state.Persistent.CurrentTerm command state.Persistent.Log
            State.updateLog newLog state
