namespace Raft

type RaftNode(config: NodeConfig, transport: ITransport, persistence: IPersistence, onApply: LogEntry -> unit) =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let cts = new System.Threading.CancellationTokenSource()
            transport.StartListener config (RaftRPC >> inbox.Post) cts.Token |> ignore
            let loadedState = persistence.Load()

            let ctx: NodeContext =
                { Config = config
                  Transport = transport
                  Persistence = persistence
                  OnApply = onApply
                  Inbox = inbox
                  State = State.init config loadedState
                  ElectionTimer = None
                  HeartbeatTimer = None }

            NodeAgent.agentLoop
                { ctx with
                    ElectionTimer = NodeTimer.resetElectionTimer ctx })

    member _.SubmitCommand cmd =
        agent.PostAndReply(fun ch -> RaftRPC(ClientCommand(cmd, Some ch)))

    member _.GetState() = agent.PostAndReply GetState
