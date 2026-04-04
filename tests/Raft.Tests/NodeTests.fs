module Raft.Tests.NodeTests

open System.Threading
open System.Threading.Tasks
open Xunit
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
        member _.SendMessage peer msg = messages.Add((peer, msg))

        member _.StartListener _ postMessage _ =
            postMessageOpt <- Some postMessage
            Task.FromResult(())

type MockPersistence() =
    let mutable stateOpt: PersistentState option = None

    interface IPersistence with
        member _.Save state = stateOpt <- Some state
        member _.Load() = stateOpt

let configForNode id port =
    { NodeId = id
      Host = "127.0.0.1"
      Port = port
      Peers = []
      ElectionTimeoutMinMs = 100
      ElectionTimeoutMaxMs = 200
      HeartbeatIntervalMs = 50 }

let configWithPeers id port =
    { NodeId = id
      Host = "127.0.0.1"
      Port = port
      Peers =
        [ { Id = 2
            Host = "127.0.0.1"
            Port = 16002 }
          { Id = 3
            Host = "127.0.0.1"
            Port = 16003 } ]
      ElectionTimeoutMinMs = 100
      ElectionTimeoutMaxMs = 200
      HeartbeatIntervalMs = 50 }

[<Fact>]
let ``Node initializes as Follower`` () =
    let config = configForNode 1 16001
    let applied = ResizeArray<LogEntry>()
    let onApply entry = applied.Add entry
    let transport = MockTransport()
    let persistence = MockPersistence()
    let node = new RaftNode(config, transport, persistence, onApply)

    let state = node.GetState()
    Assert.Equal(Follower, state.Role)
    Assert.Equal(1, state.Config.NodeId)
    Assert.Equal(0L, state.Persistent.CurrentTerm)

[<Fact>]
let ``SubmitCommand fails when not leader`` () =
    let config = configForNode 1 16002
    let transport = MockTransport()
    let persistence = MockPersistence()
    let node = new RaftNode(config, transport, persistence, ignore)

    let success = node.SubmitCommand "put a 1"
    Assert.False success

[<Fact>]
let ``Node transitions to candidate (and leader if single node) after election timeout`` () =
    let config = configForNode 1 16003
    let transport = MockTransport()
    let persistence = MockPersistence()
    let node = new RaftNode(config, transport, persistence, ignore)

    let initialState = node.GetState()
    Assert.Equal(Follower, initialState.Role)

    Thread.Sleep 500

    let stateAfterTimeout = node.GetState()
    Assert.Equal(Leader, stateAfterTimeout.Role)
    Assert.Equal(1L, stateAfterTimeout.Persistent.CurrentTerm)
    Assert.True stateAfterTimeout.LeaderState.IsSome

[<Fact>]
let ``Leader can submit commands and apply them`` () =
    let config = configForNode 1 16004
    let applied = ResizeArray<LogEntry>()
    let onApply entry = applied.Add entry
    let transport = MockTransport()
    let persistence = MockPersistence()
    let node = new RaftNode(config, transport, persistence, onApply)

    Thread.Sleep 500

    let success = node.SubmitCommand "put x 10"
    Assert.True success

    let finalState = node.GetState()
    Assert.Equal(1, finalState.Persistent.Log.Length)
    Assert.Equal("put x 10", finalState.Persistent.Log.[0].Command)

[<Fact>]
let ``Node can handle incoming Raft RPCs and broadcast messages to peers`` () =
    let config = configWithPeers 1 16005
    let transport = MockTransport()
    let persistence = MockPersistence()
    let node = new RaftNode(config, transport, persistence, ignore)

    Thread.Sleep 500

    let rv =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    transport.ReceiveMessage(RequestVoteMsg rv)

    Thread.Sleep 50

    Assert.Contains(
        transport.Messages,
        fun (p, msg) ->
            p.Id = 2
            && match msg with
               | RequestVoteResponseMsg _ -> true
               | _ -> false
    )

    Thread.Sleep 500

    Assert.Contains(
        transport.Messages,
        fun (p, msg) ->
            p.Id = 3
            && match msg with
               | RequestVoteMsg _ -> true
               | _ -> false
    )

    let stateBeforeVote = node.GetState()
    let voteTerm = stateBeforeVote.Persistent.CurrentTerm

    let voteResp =
        { VoterId = 2
          VoterTerm = voteTerm
          VoteGranted = true }

    transport.ReceiveMessage(RequestVoteResponseMsg voteResp)

    let voteResp3 =
        { VoterId = 3
          VoterTerm = voteTerm
          VoteGranted = true }

    transport.ReceiveMessage(RequestVoteResponseMsg voteResp3)

    Thread.Sleep 50

    let state = node.GetState()
    Assert.Equal(Leader, state.Role)

    Assert.Contains(
        transport.Messages,
        fun (p, msg) ->
            p.Id = 2
            && match msg with
               | AppendEntriesMsg _ -> true
               | _ -> false
    )

    let aeResp =
        { FollowerTerm = voteTerm
          Success = true
          MatchIndex = 0L
          FollowerId = 2 }

    transport.ReceiveMessage(AppendEntriesResponseMsg aeResp)

    let ae =
        { LeaderTerm = voteTerm + 1L
          LeaderId = 3
          PrevLogIndex = 0L
          PrevLogTerm = 0L
          Entries = []
          LeaderCommit = 0L }

    transport.ReceiveMessage(AppendEntriesMsg ae)

    Thread.Sleep 50

    let finalState = node.GetState()
    Assert.Equal(Follower, finalState.Role)

[<Fact>]
let ``Leader correctly broadcasts AppendEntries on heartbeat timeout and applies committed logs`` () =
    let applied = ResizeArray<LogEntry>()
    let onApply entry = applied.Add entry
    let config = configWithPeers 1 16006
    let transport = MockTransport()
    let persistence = MockPersistence()
    let node = new RaftNode(config, transport, persistence, onApply)

    Thread.Sleep 500

    let s = node.GetState()
    let term = s.Persistent.CurrentTerm

    transport.ReceiveMessage(
        RequestVoteResponseMsg
            { VoterId = 2
              VoterTerm = term
              VoteGranted = true }
    )

    Thread.Sleep 50

    Assert.Equal(Leader, node.GetState().Role)

    transport.Messages.Clear()
    let success = node.SubmitCommand("put a 42")
    Assert.True success

    Thread.Sleep 50

    Assert.Contains(
        transport.Messages,
        fun (p, msg) ->
            p.Id = 2
            && match msg with
               | AppendEntriesMsg ae -> ae.Entries.Length > 0
               | _ -> false
    )

    let aeResp2 =
        { FollowerTerm = term
          Success = true
          MatchIndex = 1L
          FollowerId = 2 }

    transport.ReceiveMessage(AppendEntriesResponseMsg aeResp2)

    let aeResp3 =
        { FollowerTerm = term
          Success = true
          MatchIndex = 1L
          FollowerId = 3 }

    transport.ReceiveMessage(AppendEntriesResponseMsg aeResp3)

    Thread.Sleep 50

    let finalState2 = node.GetState()
    Assert.Equal(1L, finalState2.Volatile.CommitIndex)
    let entry = Assert.Single applied
    Assert.Equal("put a 42", entry.Command)

    transport.Messages.Clear()

    Thread.Sleep 500

    Assert.Contains(
        transport.Messages,
        fun (p, msg) ->
            p.Id = 3
            && match msg with
               | AppendEntriesMsg _ -> true
               | _ -> false
    )

    transport.ReceiveMessage(
        AppendEntriesMsg
            { LeaderTerm = term + 1L
              LeaderId = 2
              PrevLogIndex = 1L
              PrevLogTerm = term
              Entries = []
              LeaderCommit = 1L }
    )

    Thread.Sleep 50

    Assert.Equal(Follower, node.GetState().Role)
