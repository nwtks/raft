namespace Raft

module NodeAgent =
    let postProcess ctx result =
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

    let handleGetState ctx (reply: RaftState -> unit) =
        reply ctx.State

        { State = ctx.State
          ElectionAction = Keep
          HeartbeatAction = Keep
          PendingReads = ctx.PendingReads }

    let handleLinearizableRead ctx (reply: ReadCommandResult -> unit) =
        match ctx.State.Role with
        | Leader ->
            let pendingRead: PendingRead =
                { ReadIndex = ctx.State.Volatile.CommitIndex
                  Reply = reply
                  Responses = Set.singleton ctx.State.Config.NodeId }

            NodeRead.handleLinearizableRead ctx (ctx.PendingReads @ [ pendingRead ])
        | _ ->
            let leaderInfo =
                ctx.State.CurrentLeader
                |> Option.bind (fun leaderId -> ctx.Config.Peers |> List.tryFind (fun p -> p.Id = leaderId))

            reply (ReadRedirect leaderInfo)

            { State = ctx.State
              ElectionAction = Keep
              HeartbeatAction = Keep
              PendingReads = ctx.PendingReads }

    let handleTakeSnapshot ctx data (reply: unit -> unit) =
        let result = NodeSnapshot.handleTakeSnapshot ctx data
        reply ()
        result

    let handleMessage ctx =
        function
        | ElectionTimeout -> NodeTimeout.handleElectionTimeout ctx
        | HeartbeatTimeout -> NodeTimeout.handleHeartbeatTimeout ctx
        | RaftRPC rpcMsg -> NodeRaft.handleRaftRPC ctx rpcMsg
        | GetState reply -> handleGetState ctx reply
        | LinearizableRead reply -> handleLinearizableRead ctx reply
        | TakeSnapshot(data, reply) -> handleTakeSnapshot ctx data reply
        | msg -> NodeLocal.handleLocalMessage ctx msg

    let handleShutdown ctx (reply: unit -> unit) =
        NodeTimer.disposeTimer ctx.ElectionTimer
        NodeTimer.disposeTimer ctx.HeartbeatTimer
        ctx.CancellationTokenSource.Cancel()
        reply ()

    [<TailCall>]
    let rec agentLoop ctx =
        async {
            let! msg = ctx.Inbox.Receive()

            match msg with
            | Shutdown reply -> handleShutdown ctx reply
            | _ -> return! handleMessage ctx msg |> postProcess ctx |> agentLoop
        }
