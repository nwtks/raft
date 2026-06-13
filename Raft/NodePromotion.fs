namespace Raft

module NodePromotion =
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

    let getReadyPeers state =
        match state.LeaderState with
        | Some ls ->
            let lastIndex = Log.lastIndex state.Persistent.Log

            state.NonVotingPeers
            |> List.filter (fun p -> ls.MatchIndex |> Map.tryFind p.Id |> Option.defaultValue -1L >= lastIndex)
        | None -> []

    let tryPromoteNonVotingPeers ctx state =
        if state.Role = Leader && not (List.isEmpty state.NonVotingPeers) then
            match getReadyPeers state with
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

                NodeApply.applyCommitted ctx.OnApply newState
                |> NodeSnapshot.autoSnapshotIfNeeded ctx
                |> tryFinalizeConfiguration
        else
            state
