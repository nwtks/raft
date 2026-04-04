namespace Raft

open System.Threading
open System.Threading.Tasks

type NodeMessage =
    | RaftRPC of RaftMessage
    | ElectionTimeout
    | HeartbeatTimeout
    | GetState of AsyncReplyChannel<RaftState>

type ITransport =
    abstract member StartListener:
        config: NodeConfig -> postMessage: (RaftMessage -> unit) -> ct: CancellationToken -> Task<unit>

    abstract member SendMessage: peer: PeerInfo -> msg: RaftMessage -> unit

module Node =
    type Context =
        { Config: NodeConfig
          Transport: ITransport
          Persistence: IPersistence
          OnApply: LogEntry -> unit
          Inbox: MailboxProcessor<NodeMessage>
          State: RaftState
          ElectionTimer: Timer option
          HeartbeatTimer: Timer option }

    let rand = System.Random()

    let getRandomElectionTimeout (config: NodeConfig) =
        rand.Next(config.ElectionTimeoutMinMs, config.ElectionTimeoutMaxMs)

    let resetTimer (inbox: MailboxProcessor<NodeMessage>) (timer: Timer option) (msg: NodeMessage) (interval: int) =
        match timer with
        | Some t ->
            t.Change(interval, Timeout.Infinite) |> ignore
            Some t
        | None -> Some(new Timer((fun _ -> inbox.Post msg), null, interval, Timeout.Infinite))

    let stopTimer (timer: Timer option) =
        match timer with
        | Some t -> t.Change(Timeout.Infinite, Timeout.Infinite) |> ignore
        | None -> ()

    let broadcastRequestVote (config: NodeConfig) (transport: ITransport) (state: RaftState) =
        let requestVote = Election.createRequestVote state

        for peer in config.Peers do
            transport.SendMessage peer (RequestVoteMsg requestVote)

    let broadcastHeartbeat (config: NodeConfig) (transport: ITransport) (state: RaftState) =
        for peer in config.Peers do
            match Replication.createHeartbeat peer.Id state with
            | Some hb -> transport.SendMessage peer (AppendEntriesMsg hb)
            | None -> ()

    let broadcastAppendEntries (config: NodeConfig) (transport: ITransport) (state: RaftState) =
        for peer in config.Peers do
            match Replication.createAppendEntries peer.Id state with
            | Some ae -> transport.SendMessage peer (AppendEntriesMsg ae)
            | None -> ()

    let applyCommitted (onApply: LogEntry -> unit) (state: RaftState) =
        let mutable lastApplied = state.Volatile.LastApplied

        while lastApplied < state.Volatile.CommitIndex do
            lastApplied <- lastApplied + 1L

            match Log.getEntry lastApplied state.Persistent.Log with
            | Some entry -> onApply entry
            | None -> ()

        State.updateLastApplied lastApplied state

    let receiveElectionTimeout (ctx: Context) =
        if ctx.State.Role <> Leader then
            let newState = Election.startElection ctx.State
            broadcastRequestVote ctx.Config ctx.Transport newState

            let finalState =
                if Set.count newState.VotesReceived >= State.quorumSize newState then
                    let state = State.initLeaderState newState
                    broadcastHeartbeat ctx.Config ctx.Transport state
                    state
                else
                    newState

            let electionTimer =
                resetTimer ctx.Inbox ctx.ElectionTimer ElectionTimeout (getRandomElectionTimeout ctx.Config)

            finalState, electionTimer
        else
            ctx.State, ctx.ElectionTimer

    let receiveHeartbeatTimeout (ctx: Context) =
        if ctx.State.Role = Leader then
            broadcastAppendEntries ctx.Config ctx.Transport ctx.State

            let heartbeatTimer =
                resetTimer ctx.Inbox ctx.HeartbeatTimer HeartbeatTimeout ctx.Config.HeartbeatIntervalMs

            ctx.State, heartbeatTimer
        else
            ctx.State, ctx.HeartbeatTimer

    let saveIfChanged (ctx: Context) (newState: RaftState) =
        if ctx.State.Persistent <> newState.Persistent then
            ctx.Persistence.Save newState.Persistent

    let handleRaftMessage (ctx: Context) (rpcMsg: RaftMessage) =
        match rpcMsg with
        | RequestVoteMsg requestVote ->
            let state, response = Election.handleRequestVote requestVote ctx.State
            saveIfChanged ctx state
            let peer = ctx.Config.Peers |> List.find (fun p -> p.Id = requestVote.CandidateId)
            ctx.Transport.SendMessage peer (RequestVoteResponseMsg response)
            state, true
        | RequestVoteResponseMsg response ->
            let state = Election.handleVoteResponse response.VoterId response ctx.State
            saveIfChanged ctx state
            state, false
        | AppendEntriesMsg appendEntries ->
            let state, response = Replication.handleAppendEntries appendEntries ctx.State
            saveIfChanged ctx state
            let peer = ctx.Config.Peers |> List.find (fun p -> p.Id = appendEntries.LeaderId)
            ctx.Transport.SendMessage peer (AppendEntriesResponseMsg response)
            state, true
        | AppendEntriesResponseMsg response ->
            let state = Replication.handleAppendEntriesResponse response ctx.State
            let state2 = Replication.advanceCommitIndex state
            saveIfChanged ctx state2
            state2, false
        | ClientCommand(cmd, replyChannel) ->
            if ctx.State.Role = Leader then
                let state = Replication.appendCommand cmd ctx.State
                saveIfChanged ctx state
                broadcastAppendEntries ctx.Config ctx.Transport state
                replyChannel |> Option.iter (fun ch -> ch.Reply true)
                state, false
            else
                replyChannel |> Option.iter (fun ch -> ch.Reply false)
                ctx.State, false

    let receiveRaftRPC (ctx: Context) (rpcMsg: RaftMessage) =
        let oldRole = ctx.State.Role
        let newState, sendReply = handleRaftMessage ctx rpcMsg

        if oldRole <> Leader && newState.Role = Leader then
            broadcastHeartbeat ctx.Config ctx.Transport newState

            let heartbeatTimer =
                resetTimer ctx.Inbox ctx.HeartbeatTimer HeartbeatTimeout ctx.Config.HeartbeatIntervalMs

            stopTimer ctx.ElectionTimer
            let state = applyCommitted ctx.OnApply newState
            state, ctx.ElectionTimer, heartbeatTimer
        elif oldRole = Leader && newState.Role <> Leader then
            stopTimer ctx.HeartbeatTimer

            let electionTimer =
                resetTimer ctx.Inbox ctx.ElectionTimer ElectionTimeout (getRandomElectionTimeout ctx.Config)

            let state = applyCommitted ctx.OnApply newState
            state, electionTimer, ctx.HeartbeatTimer
        elif sendReply then
            let electionTimer =
                resetTimer ctx.Inbox ctx.ElectionTimer ElectionTimeout (getRandomElectionTimeout ctx.Config)

            let state = applyCommitted ctx.OnApply newState
            state, electionTimer, ctx.HeartbeatTimer
        else
            let state = applyCommitted ctx.OnApply newState
            state, ctx.ElectionTimer, ctx.HeartbeatTimer

    [<TailCall>]
    let rec agentLoop (ctx: Context) =
        async {
            let! msg = ctx.Inbox.Receive()

            match msg with
            | ElectionTimeout ->
                let state, electionTimer = receiveElectionTimeout ctx

                return!
                    agentLoop
                        { ctx with
                            State = state
                            ElectionTimer = electionTimer }
            | HeartbeatTimeout ->
                let state, heartbeatTimer = receiveHeartbeatTimeout ctx

                return!
                    agentLoop
                        { ctx with
                            State = state
                            HeartbeatTimer = heartbeatTimer }
            | RaftRPC rpcMsg ->
                let state, electionTimer, heartbeatTimer = receiveRaftRPC ctx rpcMsg

                return!
                    agentLoop
                        { ctx with
                            State = state
                            ElectionTimer = electionTimer
                            HeartbeatTimer = heartbeatTimer }
            | GetState ch ->
                ch.Reply ctx.State
                return! agentLoop ctx
        }

type RaftNode(config: NodeConfig, transport: ITransport, persistence: IPersistence, onApply: LogEntry -> unit) =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let cts = new CancellationTokenSource()
            transport.StartListener config (RaftRPC >> inbox.Post) cts.Token |> ignore

            let electionTimer =
                Node.resetTimer inbox None ElectionTimeout (Node.getRandomElectionTimeout config)

            let loadedState = persistence.Load()

            let ctx: Node.Context =
                { Config = config
                  Transport = transport
                  Persistence = persistence
                  OnApply = onApply
                  Inbox = inbox
                  State = State.init config loadedState
                  ElectionTimer = electionTimer
                  HeartbeatTimer = None }

            Node.agentLoop ctx)

    member _.SubmitCommand(cmd: string) =
        agent.PostAndReply(fun ch -> RaftRPC(ClientCommand(cmd, Some ch)))

    member _.GetState() = agent.PostAndReply GetState
