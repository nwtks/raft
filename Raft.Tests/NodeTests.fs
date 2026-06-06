module Raft.Tests.NodeTests

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
            System.Threading.Tasks.Task.FromResult(())

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
let ``RaftNode initializes as Follower with term 0`` () =
    task {
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
    }

[<Fact>]
let ``RaftNode.SubmitCommand returns false when node is not Leader`` () =
    task {
        let config = configForNode 1 16002
        let transport = MockTransport()
        let persistence = MockPersistence()
        let node = new RaftNode(config, transport, persistence, ignore)

        let success = node.SubmitCommand "put a 1"
        Assert.False success
    }

[<Fact>]
let ``RaftNode transitions to Candidate then Leader in single-node cluster after election timeout`` () =
    task {
        let config = configForNode 1 16003
        let transport = MockTransport()
        let persistence = MockPersistence()
        let node = new RaftNode(config, transport, persistence, ignore)

        let initialState = node.GetState()
        Assert.Equal(Follower, initialState.Role)

        node.TriggerElectionTimeout()
        let stateAfterTimeout = node.GetState()
        Assert.Equal(Leader, stateAfterTimeout.Role)
        Assert.Equal(1L, stateAfterTimeout.Persistent.CurrentTerm)
        Assert.True stateAfterTimeout.LeaderState.IsSome
    }

[<Fact>]
let ``Leader RaftNode accepts submitted commands and applies them to state machine`` () =
    task {
        let config = configForNode 1 16004
        let applied = ResizeArray<LogEntry>()
        let onApply entry = applied.Add entry
        let transport = MockTransport()
        let persistence = MockPersistence()
        let node = new RaftNode(config, transport, persistence, onApply)

        node.TriggerElectionTimeout()
        let success = node.SubmitCommand "put x 10"
        Assert.True success

        let finalState = node.GetState()
        Assert.Equal(1, finalState.Persistent.Log.Length)
        Assert.Equal("put x 10", finalState.Persistent.Log.[0].Command)
    }

[<Fact>]
let ``RaftNode handles incoming RequestVote RPC and broadcasts election to peers`` () =
    task {
        let config = configWithPeers 1 16005
        let transport = MockTransport()
        let persistence = MockPersistence()
        let node = new RaftNode(config, transport, persistence, ignore)

        let rv =
            { CandidateTerm = 1L
              CandidateId = 2
              LastLogIndex = 0L
              LastLogTerm = 0L }

        transport.ReceiveMessage(RequestVoteMsg rv)
        node.GetState() |> ignore

        Assert.Contains(
            transport.Messages,
            fun (p, msg) ->
                p.Id = 2
                && match msg with
                   | RequestVoteResponseMsg _ -> true
                   | _ -> false
        )

        node.TriggerElectionTimeout()
        node.GetState() |> ignore

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
        node.GetState() |> ignore

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
              FollowerId = 2
              ConflictTerm = 0L
              ConflictIndex = 0L }

        transport.ReceiveMessage(AppendEntriesResponseMsg aeResp)

        let ae =
            { LeaderTerm = voteTerm + 1L
              LeaderId = 3
              PrevLogIndex = 0L
              PrevLogTerm = 0L
              Entries = []
              LeaderCommit = 0L }

        transport.ReceiveMessage(AppendEntriesMsg ae)
        node.GetState() |> ignore

        let finalState = node.GetState()
        Assert.Equal(Follower, finalState.Role)
    }

[<Fact>]
let ``Leader RaftNode broadcasts AppendEntries on heartbeat, processes responses, and applies committed entries`` () =
    task {
        let applied = ResizeArray<LogEntry>()
        let onApply entry = applied.Add entry
        let config = configWithPeers 1 16006
        let transport = MockTransport()
        let persistence = MockPersistence()
        let node = new RaftNode(config, transport, persistence, onApply)

        node.TriggerElectionTimeout()
        node.GetState() |> ignore

        let s = node.GetState()
        let term = s.Persistent.CurrentTerm

        transport.ReceiveMessage(
            RequestVoteResponseMsg
                { VoterId = 2
                  VoterTerm = term
                  VoteGranted = true }
        )

        node.GetState() |> ignore

        Assert.Equal(Leader, node.GetState().Role)

        transport.Messages.Clear()
        let success = node.SubmitCommand "put a 42"
        Assert.True success

        node.GetState() |> ignore

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
              FollowerId = 2
              ConflictTerm = 0L
              ConflictIndex = 0L }

        transport.ReceiveMessage(AppendEntriesResponseMsg aeResp2)

        let aeResp3 =
            { FollowerTerm = term
              Success = true
              MatchIndex = 1L
              FollowerId = 3
              ConflictTerm = 0L
              ConflictIndex = 0L }

        transport.ReceiveMessage(AppendEntriesResponseMsg aeResp3)

        node.GetState() |> ignore

        let finalState2 = node.GetState()
        Assert.Equal(1L, finalState2.Volatile.CommitIndex)
        let entry = Assert.Single applied
        Assert.Equal("put a 42", entry.Command)

        transport.Messages.Clear()

        node.TriggerHeartbeatTimeout()
        node.GetState() |> ignore

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

        node.GetState() |> ignore

        Assert.Equal(Follower, node.GetState().Role)
    }
