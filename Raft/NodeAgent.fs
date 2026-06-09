namespace Raft

module NodeAgent =
    let postProcess ctx (result: MessageResult) =
        let electionTimer = NodeTimer.applyElectionAction ctx result.ElectionAction
        let heartbeatTimer = NodeTimer.applyHeartbeatAction ctx result.HeartbeatAction

        { ctx with
            State = result.State
            ElectionTimer = electionTimer
            HeartbeatTimer = heartbeatTimer
            PendingReads = result.PendingReads
            Config =
                if result.State.Config <> ctx.Config then
                    result.State.Config
                else
                    ctx.Config }

    [<TailCall>]
    let rec agentLoop ctx =
        async {
            let! msg = ctx.Inbox.Receive()

            match msg with
            | Shutdown ch ->
                NodeTimer.disposeTimer ctx.ElectionTimer
                NodeTimer.disposeTimer ctx.HeartbeatTimer
                ctx.CancellationTokenSource.Cancel()
                ch.Reply()
            | ElectionTimeout -> return! NodeTimeout.handleElectionTimeout ctx |> postProcess ctx |> agentLoop
            | HeartbeatTimeout -> return! NodeTimeout.handleHeartbeatTimeout ctx |> postProcess ctx |> agentLoop
            | RaftRPC rpcMsg -> return! NodeRaft.handleRaftRPC ctx rpcMsg |> postProcess ctx |> agentLoop
            | GetState ch ->
                ch.Reply ctx.State
                return! agentLoop ctx
            | ClientCommand _
            | AddPeer _
            | RemovePeer _ as localMsg ->
                return! NodeLocal.handleLocalMessage ctx localMsg |> postProcess ctx |> agentLoop
            | LinearizableRead replyChannel ->
                return! NodeRead.handleLinearizableRead ctx replyChannel |> postProcess ctx |> agentLoop
            | TakeSnapshot(data, ch) ->
                return! NodeSnapshot.handleTakeSnapshot ctx data ch |> postProcess ctx |> agentLoop
        }
