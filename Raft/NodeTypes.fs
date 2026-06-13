namespace Raft

type ClientCommandResult =
    | Accepted
    | Redirect of leader: PeerInfo option

type ReadCommandResult =
    | ReadReady
    | ReadRedirect of PeerInfo option

type PendingRead =
    { ReadIndex: LogIndex
      Reply: ReadCommandResult -> unit
      Responses: Set<NodeId> }

type TimerAction =
    | Keep
    | Reset
    | Stop

type MessageResult =
    { State: RaftState
      ElectionAction: TimerAction
      HeartbeatAction: TimerAction
      PendingReads: PendingRead list }

type NodeMessage =
    | RaftRPC of RaftMessage
    | ElectionTimeout
    | HeartbeatTimeout
    | GetState of (RaftState -> unit)
    | ClientCommand of command: string * clientId: string option * seqNum: int64 option * (ClientCommandResult -> unit)
    | LinearizableRead of (ReadCommandResult -> unit)
    | TakeSnapshot of data: string * (unit -> unit)
    | AddPeer of PeerInfo * (bool -> unit)
    | RemovePeer of NodeId * (bool -> unit)
    | Shutdown of (unit -> unit)

type ITransport =
    abstract member StartListener:
        config: NodeConfig ->
        postMessage: (RaftMessage -> unit) ->
        ct: System.Threading.CancellationToken ->
            Async<unit>

    abstract member SendMessage: peer: PeerInfo -> msg: RaftMessage -> Async<unit>

type NodeContext =
    { Config: NodeConfig
      Transport: ITransport
      Persistence: IPersistence
      OnApply: LogEntry -> unit
      OnInstallSnapshot: string -> unit
      OnGetSnapshotData: unit -> string
      Inbox: MailboxProcessor<NodeMessage>
      State: RaftState
      ElectionTimer: System.Threading.Timer option
      HeartbeatTimer: System.Threading.Timer option
      CancellationTokenSource: System.Threading.CancellationTokenSource
      PendingReads: PendingRead list }
