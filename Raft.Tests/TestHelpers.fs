module Raft.Tests.TestHelpers

open Raft

type MockTransport() =
    let messages = ResizeArray<PeerInfo * RaftMessage>()
    let mutable postMessageOpt: (RaftMessage -> unit) option = None
    member _.Messages = messages

    member _.ReceiveMessage msg =
        match postMessageOpt with
        | Some cb -> cb msg
        | None -> ()

    interface ITransport with
        member _.SendMessage peer msg =
            messages.Add((peer, msg))
            async { return () }

        member _.StartListener _ postMessage _ =
            postMessageOpt <- Some postMessage
            async { return () }

type MockPersistence() =
    let mutable stateOpt: PersistentState option = None

    interface IPersistence with
        member _.Save state = stateOpt <- Some state
        member _.Load() = stateOpt

let dummyConfig =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = 5001
      Peers =
        [ { Id = 2
            Host = "127.0.0.1"
            Port = 5002 }
          { Id = 3
            Host = "127.0.0.1"
            Port = 5003 } ]
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500
      SnapshotAutoThreshold = 0 }

let dummyConfigStandalone =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = 15001
      Peers = []
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500
      SnapshotAutoThreshold = 0 }

let dummyConfigWithPort port =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = port
      Peers = []
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500
      SnapshotAutoThreshold = 0 }

let private fastTimeoutConfig =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = 0
      Peers = []
      ElectionTimeoutMinMs = 100
      ElectionTimeoutMaxMs = 200
      HeartbeatIntervalMs = 50
      SnapshotAutoThreshold = 0 }

let configForNode id _port = { fastTimeoutConfig with NodeId = id }

let configWithPeers id _port =
    { fastTimeoutConfig with
        NodeId = id
        Peers =
            [ { Id = 2; Host = "127.0.0.1"; Port = 0 }
              { Id = 3; Host = "127.0.0.1"; Port = 0 } ] }

let dummyPeer port =
    { Id = 2
      Host = "127.0.0.1"
      Port = port }

let logFromList (entries: LogEntry list) =
    entries |> List.map (fun e -> e.Index, e) |> Map.ofList

let createEntry index term cmd =
    { Index = index
      Term = term
      Command = cmd
      ClientId = None
      SeqNum = None }

let makeNode config =
    let transport = MockTransport()
    let persistence = MockPersistence()
    let node = new RaftNode(config, transport, persistence, ignore)
    node, transport, persistence

let makeNodeWithApply config onApply =
    let transport = MockTransport()
    let persistence = MockPersistence()
    let node = new RaftNode(config, transport, persistence, onApply)
    node, transport, persistence

let becomeLeader (node: RaftNode) (transport: MockTransport) =
    node.TriggerElectionTimeout()
    let term = (node.GetState()).Persistent.CurrentTerm

    transport.ReceiveMessage(
        RequestVoteResponseMsg
            { VoterId = 2
              VoterTerm = term
              VoteGranted = true }
    )

    transport.ReceiveMessage(
        RequestVoteResponseMsg
            { VoterId = 3
              VoterTerm = term
              VoteGranted = true }
    )

    node.GetState() |> ignore
    term

let threeNodeBaseConfig =
    { NodeId = 1
      Host = ""
      Port = 0
      Peers = [ { Id = 2; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ]
      ElectionTimeoutMinMs = 1
      ElectionTimeoutMaxMs = 2
      HeartbeatIntervalMs = 1
      SnapshotAutoThreshold = 0 }

let threeNodeConfigs () =
    let c1 = threeNodeBaseConfig

    let c2 =
        { threeNodeBaseConfig with
            NodeId = 2
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ] }

    let c3 =
        { threeNodeBaseConfig with
            NodeId = 3
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 2; Host = ""; Port = 0 } ] }

    c1, c2, c3

let initThreeNodes () =
    let c1, c2, c3 = threeNodeConfigs ()
    State.init c1 None, State.init c2 None, State.init c3 None

let electLeader (s1: RaftState) (s2: RaftState) (s3: RaftState) =
    let s1 = Election.startElection s1
    let rv = Election.createRequestVote s1
    let s2, resp2 = Election.handleRequestVote rv s2
    let s3, resp3 = Election.handleRequestVote rv s3
    let s1 = Election.handleVoteResponse 2 resp2 s1
    let s1 = Election.handleVoteResponse 3 resp3 s1
    s1, s2, s3, s1.Persistent.CurrentTerm

let replicateTo (followerId: NodeId) (leader: RaftState) (follower: RaftState) =
    let ae = (Replication.createAppendEntries followerId leader).Value
    let follower, resp = Replication.handleAppendEntries ae follower
    let leader = Replication.handleAppendEntriesResponse resp leader
    leader, follower

let makeNodeContextWithTransport (state: RaftState) =
    let transport = MockTransport()
    let inbox = new MailboxProcessor<NodeMessage>(fun _ -> async { () })

    let ctx: NodeContext =
        { Config = dummyConfig
          Transport = transport :> ITransport
          Persistence = MockPersistence() :> IPersistence
          OnApply = ignore
          OnInstallSnapshot = ignore
          OnGetSnapshotData = fun () -> ""
          Inbox = inbox
          State = state
          ElectionTimer = None
          HeartbeatTimer = None
          CancellationTokenSource = new System.Threading.CancellationTokenSource()
          PendingReads = [] }

    ctx, transport

let makeNodeContext (state: RaftState) =
    let ctx, _ = makeNodeContextWithTransport state
    ctx

let makeDefaultNodeContext () =
    let state = State.init dummyConfig None
    makeNodeContext state
