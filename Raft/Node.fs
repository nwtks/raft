namespace Raft

type RaftNode
    (
        config: NodeConfig,
        transport: ITransport,
        persistence: IPersistence,
        onApply: LogEntry -> unit,
        ?onInstallSnapshot: string -> unit,
        ?onGetSnapshotData: unit -> string
    ) =
    let onInstallSnapshotFn = defaultArg onInstallSnapshot ignore
    let onGetSnapshotDataFn = defaultArg onGetSnapshotData (fun () -> "")
    let cts = new System.Threading.CancellationTokenSource()

    let agent =
        new MailboxProcessor<NodeMessage>(fun inbox ->
            let loadedState = persistence.Load()

            let ctx: NodeContext =
                { Config = config
                  Transport = transport
                  Persistence = persistence
                  OnApply = onApply
                  OnInstallSnapshot = onInstallSnapshotFn
                  OnGetSnapshotData = onGetSnapshotDataFn
                  Inbox = inbox
                  State = State.init config loadedState
                  ElectionTimer = None
                  HeartbeatTimer = None
                  CancellationTokenSource = cts
                  PendingReads = [] }

            NodeAgent.agentLoop
                { ctx with
                    ElectionTimer = NodeTimer.resetElectionTimer ctx })

    do
        try
            transport.StartListener config (RaftRPC >> agent.Post) cts.Token |> ignore
        with _ ->
            (agent :> System.IDisposable).Dispose()
            cts.Dispose()
            reraise ()

    do agent.Start()

    member _.SubmitCommand cmd =
        agent.PostAndReply(fun ch -> ClientCommand(cmd, ch))

    member _.LinearizableRead() =
        agent.PostAndReply(fun ch -> LinearizableRead ch)

    member _.SubmitTakeSnapshot data =
        agent.PostAndReply(fun ch -> TakeSnapshot(data, ch))

    member _.AddPeer peerInfo =
        agent.PostAndReply(fun ch -> AddPeer(peerInfo, ch))

    member _.RemovePeer peerId =
        agent.PostAndReply(fun ch -> RemovePeer(peerId, ch))

    member _.GetState() = agent.PostAndReply GetState

    member _.TriggerElectionTimeout() = agent.Post ElectionTimeout

    member _.TriggerHeartbeatTimeout() = agent.Post HeartbeatTimeout

    interface System.IDisposable with
        member _.Dispose() =
            try
                agent.PostAndReply(fun ch -> Shutdown ch)
            finally
                cts.Dispose()
                (agent :> System.IDisposable).Dispose()
