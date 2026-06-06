namespace Raft

type NodeMessage =
    | RaftRPC of RaftMessage
    | ElectionTimeout
    | HeartbeatTimeout
    | GetState of AsyncReplyChannel<RaftState>
    | ClientCommand of command: string * AsyncReplyChannel<bool>
    | TakeSnapshot of data: string * AsyncReplyChannel<unit>
    | AddPeer of PeerInfo * AsyncReplyChannel<bool>
    | RemovePeer of NodeId * AsyncReplyChannel<bool>
    | Shutdown of AsyncReplyChannel<unit>

type ITransport =
    abstract member StartListener:
        config: NodeConfig ->
        postMessage: (RaftMessage -> unit) ->
        ct: System.Threading.CancellationToken ->
            System.Threading.Tasks.Task<unit>

    abstract member SendMessage: peer: PeerInfo -> msg: RaftMessage -> System.Threading.Tasks.Task<unit>

type NodeContext =
    { Config: NodeConfig
      Transport: ITransport
      Persistence: IPersistence
      OnApply: LogEntry -> unit
      OnInstallSnapshot: string -> unit
      Inbox: MailboxProcessor<NodeMessage>
      State: RaftState
      ElectionTimer: System.Threading.Timer option
      HeartbeatTimer: System.Threading.Timer option
      CancellationTokenSource: System.Threading.CancellationTokenSource }
