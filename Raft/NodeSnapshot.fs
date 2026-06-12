namespace Raft

module NodeSnapshot =
    let handleTakeSnapshot ctx data =
        let lastApplied = ctx.State.Volatile.LastApplied
        let lastTerm = Log.termAt lastApplied ctx.State.Persistent.Log
        let state = State.takeSnapshot lastApplied lastTerm data ctx.State
        NodeUtil.saveIfChanged ctx state

        { State = state
          ElectionAction = Keep
          HeartbeatAction = Keep
          PendingReads = ctx.PendingReads }

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
