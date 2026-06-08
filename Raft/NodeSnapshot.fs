namespace Raft

module NodeSnapshot =
    let autoSnapshotIfNeeded ctx state =
        if ctx.Config.SnapshotAutoThreshold > 0 then
            let lastSnapIndex =
                match state.Persistent.Snapshot with
                | Some snap -> snap.LastIncludedIndex
                | None -> Log.initialLogIndex

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
