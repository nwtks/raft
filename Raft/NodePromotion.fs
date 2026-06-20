namespace Raft

module NodePromotion =
    let isLastEntryFinalChange state =
        match Log.getEntry (Log.lastIndex state.Persistent.Log) state.Persistent.Log with
        | Some entry when entry.Command.StartsWith ConfigChange.ConfigCommandPrefix ->
            match ConfigChange.parse entry.Command with
            | Some(FinalChange _) -> true
            | _ -> false
        | _ -> false

    let tryFinalizeConfiguration state =
        match state.ConfigPhase, state.Role with
        | JointPhase(_, newPeers), Leader ->
            if isLastEntryFinalChange state then
                state
            else
                Replication.appendFinalConfiguration newPeers state
        | _ -> state

    let getReadyPeers state =
        match state.LeaderState with
        | Some ls ->
            let lastIndex = Log.lastIndex state.Persistent.Log

            state.NonVotingPeers
            |> List.filter (fun p -> ls.MatchIndex |> Map.tryFind p.Id |> Option.defaultValue -1L >= lastIndex)
        | None -> []

    let promoteReadyNonVotingPeers state =
        match getReadyPeers state with
        | [] -> state
        | readyPeers ->
            let caughtUpIds = readyPeers |> List.map (fun p -> p.Id) |> Set.ofList

            let remainingNonVoting =
                state.NonVotingPeers |> List.filter (fun p -> not (caughtUpIds.Contains p.Id))

            let oldPeers = state.Config.Peers
            let allVoting = List.append oldPeers readyPeers

            { state with
                NonVotingPeers = remainingNonVoting }
            |> Replication.appendJointConsensus oldPeers allVoting

    let applyAndCompact ctx state =
        state
        |> NodeApply.applyCommitted ctx.OnApply
        |> NodeSnapshot.autoSnapshotIfNeeded ctx
        |> tryFinalizeConfiguration

    let tryPromoteNonVotingPeers ctx state =
        if state.Role = Leader && not (List.isEmpty state.NonVotingPeers) then
            let newState = promoteReadyNonVotingPeers state

            if newState = state then
                state
            else
                NodeUtil.saveIfChanged ctx newState
                NodeBroadcaster.broadcastAppendEntries ctx.Config ctx.Transport newState
                applyAndCompact ctx newState
        else
            state
