namespace Raft

module NodeApply =
    let applyConfigChangeEntry entry state =
        let stateWithSession =
            State.updateSessionIfNewer state.Persistent.SessionTable entry.ClientId entry.SeqNum state

        let stateWithIndex = State.updateLastConfigIndex entry.Index stateWithSession

        match ConfigChange.parse entry.Command with
        | Some(JointChange(oldPeers, newPeers)) -> State.enterJointConsensus oldPeers newPeers stateWithIndex
        | Some(FinalChange newPeers) -> State.exitJointConsensus newPeers stateWithIndex
        | None ->
            let json = entry.Command.Substring ConfigChange.ConfigCommandPrefix.Length
            let newPeers = System.Text.Json.JsonSerializer.Deserialize<PeerInfo list> json

            if isNull (box newPeers) then
                ConfigChange.log $"Cannot parse config change command at index {entry.Index}, ignoring: {entry.Command}"
                stateWithIndex
            else
                State.updateConfig newPeers stateWithIndex

    let applyNormalEntry onApply entry state =
        let isDuplicate =
            State.isDuplicateSession state.Persistent.SessionTable entry.ClientId entry.SeqNum

        let newState =
            State.updateSessionIfNewer state.Persistent.SessionTable entry.ClientId entry.SeqNum state

        if not isDuplicate then
            onApply entry

        newState

    let applyOneEntry onApply state entry =
        if entry.Command.StartsWith ConfigChange.ConfigCommandPrefix then
            applyConfigChangeEntry entry state
        elif entry.Command = Log.NoOpCommand then
            state
        else
            applyNormalEntry onApply entry state

    [<TailCall>]
    let rec loopApplyCommitted onApply state lastApplied =
        if lastApplied < state.Volatile.CommitIndex then
            let next = lastApplied + 1L

            match Log.getEntry next state.Persistent.Log with
            | Some entry -> loopApplyCommitted onApply (applyOneEntry onApply state entry) next
            | None -> loopApplyCommitted onApply state next
        else
            state, lastApplied

    let applyCommitted onApply state =
        let newState, lastApplied =
            loopApplyCommitted onApply state state.Volatile.LastApplied

        State.updateLastApplied lastApplied newState
