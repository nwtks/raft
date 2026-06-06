namespace Raft

type RaftNode
    (
        config: NodeConfig,
        transport: ITransport,
        persistence: IPersistence,
        onApply: LogEntry -> unit,
        ?onInstallSnapshot: string -> unit
    ) =
    let onInstallSnapshotFn = defaultArg onInstallSnapshot ignore
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
                  Inbox = inbox
                  State = State.init config loadedState
                  ElectionTimer = None
                  HeartbeatTimer = None
                  CancellationTokenSource = cts }

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
        agent.PostAndReply(fun ch -> RaftRPC(ClientCommand(cmd, Some ch)))

    member _.SubmitTakeSnapshot data =
        agent.PostAndReply(fun ch -> TakeSnapshot(data, ch))

    member _.AddPeer peerInfo =
        agent.PostAndReply(fun ch -> RaftRPC(AddPeer(peerInfo, Some ch)))

    member _.RemovePeer peerId =
        agent.PostAndReply(fun ch -> RaftRPC(RemovePeer(peerId, Some ch)))

    member _.GetState() = agent.PostAndReply GetState

    member _.TriggerElectionTimeout() = agent.Post ElectionTimeout

    member _.TriggerHeartbeatTimeout() = agent.Post HeartbeatTimeout

    interface System.IDisposable with
        member _.Dispose() =
            agent.PostAndReply(fun ch -> Shutdown ch)
            cts.Dispose()
            (agent :> System.IDisposable).Dispose()
