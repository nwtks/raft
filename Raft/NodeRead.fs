namespace Raft

module NodeRead =
    let canServePendingRead state pendingRead =
        state.Role = Leader
        && state.Volatile.CommitIndex > Log.initialLogIndex
        && Log.termAt state.Volatile.CommitIndex state.Persistent.Log = state.Persistent.CurrentTerm
        && State.hasQuorum pendingRead.Responses state

    let classifyPendingReads pendingReads state =
        pendingReads
        |> List.fold
            (fun (remaining, resolved) pendingRead ->
                if state.Role = Leader then
                    if
                        canServePendingRead state pendingRead
                        && state.Volatile.LastApplied >= pendingRead.ReadIndex
                    then
                        remaining, (pendingRead, ReadReady) :: resolved
                    else
                        pendingRead :: remaining, resolved
                else
                    let leaderInfo =
                        state.CurrentLeader
                        |> Option.bind (fun leaderId -> state.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))

                    remaining, (pendingRead, ReadRedirect leaderInfo) :: resolved)
            ([], [])
        |> fun (remaining, resolved) -> List.rev remaining, List.rev resolved

    let processPendingReads pendingReads state =
        let remaining, resolved = classifyPendingReads pendingReads state
        resolved |> List.iter (fun (pr, result) -> pr.ReplyChannel.Reply result)
        remaining

    let handleLinearizableRead ctx (pendingReads: PendingRead list) =
        NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport ctx.State
        let remainingReads = processPendingReads pendingReads ctx.State

        { State = ctx.State
          ElectionAction = Keep
          HeartbeatAction = Keep
          PendingReads = remainingReads }
