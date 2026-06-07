namespace Raft

module NodeAgent =
    [<TailCall>]
    let rec agentLoop ctx =
        async {
            let! msg = ctx.Inbox.Receive()

            match msg with
            | ElectionTimeout ->
                let state, electionTimer = NodeTimeout.receiveElectionTimeout ctx
                let remainingReads = NodeApply.processPendingReads ctx.PendingReads state

                return!
                    agentLoop
                        { ctx with
                            State = state
                            ElectionTimer = electionTimer
                            PendingReads = remainingReads }
            | HeartbeatTimeout ->
                let state, heartbeatTimer = NodeTimeout.receiveHeartbeatTimeout ctx

                return!
                    agentLoop
                        { ctx with
                            State = state
                            HeartbeatTimer = heartbeatTimer }
            | RaftRPC rpcMsg ->
                let state, electionTimer, heartbeatTimer = NodeRaft.receiveRaftRPC ctx rpcMsg

                let newCtx =
                    NodeRaft.handleRaftRPCResult ctx rpcMsg state electionTimer heartbeatTimer

                return! agentLoop newCtx
            | GetState ch ->
                ch.Reply ctx.State
                return! agentLoop ctx
            | ClientCommand _
            | AddPeer _
            | RemovePeer _ as localMsg -> return! agentLoop (NodeLocal.handleLocalMessageResult ctx localMsg)
            | LinearizableRead replyChannel -> return! agentLoop (NodeApply.handleLinearizableRead ctx replyChannel)
            | TakeSnapshot(data, ch) ->
                let lastApplied = ctx.State.Volatile.LastApplied
                let lastTerm = Log.termAt lastApplied ctx.State.Persistent.Log
                let state = State.takeSnapshot lastApplied lastTerm data ctx.State
                NodeUtil.saveIfChanged ctx state
                ch.Reply()
                return! agentLoop { ctx with State = state }
            | Shutdown ch ->
                NodeTimer.disposeTimer ctx.ElectionTimer
                NodeTimer.disposeTimer ctx.HeartbeatTimer
                ctx.CancellationTokenSource.Cancel()
                ch.Reply()
        }
