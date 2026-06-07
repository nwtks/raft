namespace Raft

module NodePromotion =
    let autoSnapshotIfNeeded ctx state =
        if ctx.Config.SnapshotAutoThreshold > 0 then
            let lastSnapIndex =
                match state.Persistent.Snapshot with
                | Some snap -> snap.LastIncludedIndex
                | None -> 0L

            let logEntryCount = Log.lastIndex state.Persistent.Log - lastSnapIndex

            if logEntryCount >= int64 ctx.Config.SnapshotAutoThreshold then
                let lastApplied = state.Volatile.LastApplied
                let lastTerm = Log.termAt lastApplied state.Persistent.Log
                let data = ctx.OnGetSnapshotData()
                let newState = State.takeSnapshot lastApplied lastTerm data state
                NodeUtil.saveIfChanged ctx newState
                newState
            else
                state
        else
            state

    let tryFinalizeConfiguration state =
        match state.ConfigPhase, state.Role with
        | JointPhase(_, newPeers), Leader ->
            let lastEntry =
                Log.getEntry (Log.lastIndex state.Persistent.Log) state.Persistent.Log

            match lastEntry with
            | Some entry when entry.Command.StartsWith ConfigChange.ConfigCommandPrefix ->
                match ConfigChange.parse entry.Command with
                | Some(FinalChange _) -> state
                | _ -> Replication.appendFinalConfiguration newPeers state
            | _ -> Replication.appendFinalConfiguration newPeers state
        | _ -> state

    let tryPromoteNonVotingPeers ctx state =
        if state.Role = Leader && not (List.isEmpty state.NonVotingPeers) then
            let lastIndex = Log.lastIndex state.Persistent.Log

            let readyPeers =
                state.NonVotingPeers
                |> List.filter (fun p ->
                    match state.LeaderState with
                    | Some ls -> ls.MatchIndex |> Map.tryFind p.Id |> Option.defaultValue -1L >= lastIndex
                    | None -> false)

            match readyPeers with
            | [] -> state
            | readyPeers ->
                let caughtUpIds = readyPeers |> List.map (fun p -> p.Id) |> Set.ofList

                let remainingNonVoting =
                    state.NonVotingPeers |> List.filter (fun p -> not (caughtUpIds.Contains p.Id))

                let oldPeers = state.Config.Peers
                let allVoting = List.append oldPeers readyPeers

                let newState =
                    { state with
                        NonVotingPeers = remainingNonVoting }
                    |> Replication.appendJointConsensus oldPeers allVoting

                NodeUtil.saveIfChanged ctx newState
                NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport newState
                let appliedState = NodeApply.applyCommitted ctx.OnApply newState
                tryFinalizeConfiguration (autoSnapshotIfNeeded ctx appliedState)
        else
            state
