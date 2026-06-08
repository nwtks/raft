namespace Raft

module NodeApply =
    let applyConfigChangeEntry entry state =
        let stateWithSession =
            match entry.ClientId, entry.SeqNum with
            | Some cId, Some sNum when
                state.Persistent.SessionTable |> Map.tryFind cId |> Option.defaultValue -1L < sNum
                ->
                State.updateSessionTable cId sNum state
            | _ -> state

        match ConfigChange.parse entry.Command with
        | Some(JointChange(oldPeers, newPeers)) -> State.enterJointConsensus oldPeers newPeers stateWithSession
        | Some(FinalChange newPeers) -> State.exitJointConsensus newPeers stateWithSession
        | None ->
            let json = entry.Command.Substring ConfigChange.ConfigCommandPrefix.Length
            let newPeers = System.Text.Json.JsonSerializer.Deserialize<PeerInfo list> json
            State.updateConfig newPeers stateWithSession

    let applyNormalEntry onApply entry state =
        let isDuplicate =
            match entry.ClientId, entry.SeqNum with
            | Some cId, Some sNum ->
                state.Persistent.SessionTable |> Map.tryFind cId |> Option.defaultValue -1L
                >= sNum
            | _ -> false

        let newState =
            if isDuplicate then
                state
            elif entry.ClientId.IsSome && entry.SeqNum.IsSome then
                State.updateSessionTable entry.ClientId.Value entry.SeqNum.Value state
            else
                state

        if not isDuplicate then
            onApply entry

        newState

    [<TailCall>]
    let rec loopApplyCommitted onApply state lastApplied =
        if lastApplied < state.Volatile.CommitIndex then
            let next = lastApplied + 1L

            match Log.getEntry next state.Persistent.Log with
            | Some entry when entry.Command.StartsWith ConfigChange.ConfigCommandPrefix ->
                let newState = applyConfigChangeEntry entry state
                loopApplyCommitted onApply newState next
            | Some entry when entry.Command = Log.NoOpCommand -> loopApplyCommitted onApply state next
            | Some entry ->
                let newState = applyNormalEntry onApply entry state
                loopApplyCommitted onApply newState next
            | None -> loopApplyCommitted onApply state next
        else
            state, lastApplied

    let applyCommitted onApply state =
        let newState, lastApplied =
            loopApplyCommitted onApply state state.Volatile.LastApplied

        State.updateLastApplied lastApplied newState
