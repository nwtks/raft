namespace Raft

module NodeRead =
    let canServePendingRead state pendingRead =
        state.Role = Leader
        && state.Volatile.CommitIndex > Log.initialLogIndex
        && Log.termAt state.Volatile.CommitIndex state.Persistent.Log = state.Persistent.CurrentTerm
        && State.hasQuorum pendingRead.Responses state

    let processPendingReads pendingReads state =
        pendingReads
        |> List.filter (fun pendingRead ->
            if state.Role <> Leader then
                let leaderInfo =
                    state.CurrentLeader
                    |> Option.bind (fun leaderId -> state.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))

                pendingRead.ReplyChannel.Reply(ReadRedirect leaderInfo)
                false
            elif
                canServePendingRead state pendingRead
                && state.Volatile.LastApplied >= pendingRead.ReadIndex
            then
                pendingRead.ReplyChannel.Reply ReadReady
                false
            else
                true)

    let handleLinearizableRead ctx (replyChannel: AsyncReplyChannel<ReadCommandResult>) =
        if ctx.State.Role = Leader then
            let readIndex = ctx.State.Volatile.CommitIndex
            NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport ctx.State

            let pendingRead =
                { ReadIndex = readIndex
                  ReplyChannel = replyChannel
                  Responses = Set.singleton ctx.State.Config.NodeId }

            let remainingReads =
                processPendingReads (ctx.PendingReads @ [ pendingRead ]) ctx.State

            { ctx with
                PendingReads = remainingReads }
        else
            let leaderInfo =
                ctx.State.CurrentLeader
                |> Option.bind (fun leaderId -> ctx.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))

            ReadRedirect leaderInfo |> replyChannel.Reply
            ctx
