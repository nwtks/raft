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
            | Some entry when entry.Command = "" -> loopApplyCommitted onApply state next
            | Some entry ->
                let newState = applyNormalEntry onApply entry state
                loopApplyCommitted onApply newState next
            | None -> loopApplyCommitted onApply state (next + 1L)
        else
            state, lastApplied

    let applyCommitted onApply state =
        let newState, lastApplied =
            loopApplyCommitted onApply state state.Volatile.LastApplied

        State.updateLastApplied lastApplied newState

    let canServePendingRead state pendingRead =
        state.Role = Leader
        && state.Volatile.CommitIndex > 0L
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
