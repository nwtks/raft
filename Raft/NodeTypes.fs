namespace Raft

type NodeMessage =
    | RaftRPC of RaftMessage
    | ElectionTimeout
    | HeartbeatTimeout
    | GetState of AsyncReplyChannel<RaftState>

type ITransport =
    abstract member StartListener:
        config: NodeConfig ->
        postMessage: (RaftMessage -> unit) ->
        ct: System.Threading.CancellationToken ->
            System.Threading.Tasks.Task<unit>

    abstract member SendMessage: peer: PeerInfo -> msg: RaftMessage -> unit

type NodeContext =
    { Config: NodeConfig
      Transport: ITransport
      Persistence: IPersistence
      OnApply: LogEntry -> unit
      Inbox: MailboxProcessor<NodeMessage>
      State: RaftState
      ElectionTimer: System.Threading.Timer option
      HeartbeatTimer: System.Threading.Timer option }
