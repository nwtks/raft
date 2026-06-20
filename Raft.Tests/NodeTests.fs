module Raft.Tests.NodeTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``RaftNode initializes as Follower with term 0`` () =
    task {
        let node, _, _ = makeNode (configForNode 1 0)
        let state = node.GetState()
        Assert.Equal(Follower, state.Role)
        Assert.Equal(1, state.Config.NodeId)
        Assert.Equal(0L, state.Persistent.CurrentTerm)
    }

[<Fact>]
let ``RaftNode constructor throws SocketException on StartListener failure`` () =
    let transport =
        { new ITransport with
            member _.SendMessage _ _ = async { return () }

            member _.StartListener _ _ _ =
                raise (System.Net.Sockets.SocketException()) }

    Assert.Throws<System.Net.Sockets.SocketException>(fun () ->
        new RaftNode(dummyConfig, transport, MockPersistence(), ignore) |> ignore)

[<Fact>]
let ``RaftNode constructor throws IOException on StartListener failure`` () =
    let transport =
        { new ITransport with
            member _.SendMessage _ _ = async { return () }

            member _.StartListener _ _ _ =
                raise (System.IO.IOException "test IO error") }

    Assert.Throws<System.IO.IOException>(fun () ->
        new RaftNode(dummyConfig, transport, MockPersistence(), ignore) |> ignore)

[<Fact>]
let ``RaftNode submits command, broadcasts to peers, and applies committed entries`` () =
    task {
        let applied = ResizeArray<LogEntry>()

        let node, transport, _ =
            makeNodeWithApply (configWithPeers 1 0) (fun e -> applied.Add e)

        let term = becomeLeader node transport
        transport.Messages.Clear()
        Assert.Equal(Accepted, node.SubmitCommand "put a 42")
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
              MatchIndex = 2L
              FollowerId = 2
              ConflictTerm = 0L
              ConflictIndex = 0L }

        transport.ReceiveMessage(AppendEntriesResponseMsg aeResp2)

        let aeResp3 =
            { FollowerTerm = term
              Success = true
              MatchIndex = 2L
              FollowerId = 3
              ConflictTerm = 0L
              ConflictIndex = 0L }

        transport.ReceiveMessage(AppendEntriesResponseMsg aeResp3)
        let finalState2 = node.GetState()
        Assert.Equal(2L, finalState2.Volatile.CommitIndex)

        let entry = Assert.Single applied
        Assert.Equal("put a 42", entry.Command)

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

        Assert.Equal(Follower, node.GetState().Role)
    }

[<Fact>]
let ``RaftNode.SubmitCommandWithSession accepts command with session when leader`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let _ = becomeLeader node transport
        let result = node.SubmitCommandWithSession("cmd", "client1", 1L)
        Assert.Equal(Accepted, result)
    }

[<Fact>]
let ``RaftNode.SubmitCommandWithSession returns Redirect when not leader`` () =
    task {
        let node, _, _ = makeNode (configWithPeers 1 0)
        let result = node.SubmitCommandWithSession("cmd", "client1", 1L)

        match result with
        | Redirect _ -> ()
        | Accepted -> Assert.Fail "Expected Redirect but got Accepted"
    }

[<Fact>]
let ``RaftNode.LinearizableRead returns ReadRedirect when not leader`` () =
    task {
        let node, _, _ = makeNode (configWithPeers 1 0)
        let result = node.LinearizableRead()

        match result with
        | ReadRedirect _ -> ()
        | ReadReady -> Assert.Fail "Expected ReadRedirect but got ReadReady"
    }

[<Fact>]
let ``RaftNode completes LinearizableRead after committing entry in current term`` () =
    task {
        let applied = ResizeArray<LogEntry>()

        let node, transport, _ =
            makeNodeWithApply (configWithPeers 1 0) (fun e -> applied.Add e)

        let term = becomeLeader node transport

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 1L
                  FollowerId = 2
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 1L
                  FollowerId = 3
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        let state = node.GetState()
        Assert.Equal(Leader, state.Role)
        Assert.Equal(1L, state.Volatile.CommitIndex)
        Assert.Equal(1L, state.Volatile.LastApplied)

        let readResult = ref Unchecked.defaultof<ReadCommandResult>
        let readDone = new System.Threading.ManualResetEventSlim false

        node.PostLinearizableRead(fun r ->
            readResult.Value <- r
            readDone.Set())

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 1L
                  FollowerId = 2
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 1L
                  FollowerId = 3
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        Assert.True(readDone.Wait 5000)
        Assert.Equal(ReadReady, readResult.Value)
    }

[<Fact>]
let ``RaftNode queues LinearizableRead until committed entry in current term is applied`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let term = becomeLeader node transport
        let readResult = ref Unchecked.defaultof<ReadCommandResult>
        let readDone = new System.Threading.ManualResetEventSlim false

        node.PostLinearizableRead(fun r ->
            readResult.Value <- r
            readDone.Set())

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 1L
                  FollowerId = 2
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 1L
                  FollowerId = 3
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        Assert.True(readDone.Wait 5000)
        Assert.Equal(ReadReady, readResult.Value)
    }

[<Fact>]
let ``RaftNode stepping down resolves pending linearizable reads with redirect`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let term = becomeLeader node transport
        let readResult = ref Unchecked.defaultof<ReadCommandResult>
        let readDone = new System.Threading.ManualResetEventSlim false

        node.PostLinearizableRead(fun r ->
            readResult.Value <- r
            readDone.Set())

        transport.ReceiveMessage(
            AppendEntriesMsg
                { LeaderTerm = term + 1L
                  LeaderId = 2
                  PrevLogIndex = 0L
                  PrevLogTerm = 0L
                  Entries = []
                  LeaderCommit = 0L }
        )

        Assert.True(readDone.Wait 5000)

        match readResult.Value with
        | ReadRedirect _ -> ()
        | ReadReady -> Assert.Fail "Expected ReadRedirect but got ReadReady"
    }

[<Fact>]
let ``RaftNode.SubmitTakeSnapshot creates snapshot`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let term = becomeLeader node transport
        Assert.Equal(Accepted, node.SubmitCommand "put x 1")

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 2L
                  FollowerId = 2
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 2L
                  FollowerId = 3
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        let stateBefore = node.GetState()
        Assert.True(stateBefore.Volatile.LastApplied >= 1L)
        Assert.True stateBefore.Persistent.Snapshot.IsNone

        node.SubmitTakeSnapshot "test_snapshot_data"
        let stateAfter = node.GetState()
        Assert.True stateAfter.Persistent.Snapshot.IsSome

        let snap = stateAfter.Persistent.Snapshot.Value
        Assert.True(snap.LastIncludedIndex >= 1L)
        Assert.Equal("test_snapshot_data", snap.StateMachineData)
    }

[<Fact>]
let ``RaftNode.AddPeer returns true when leader`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let _ = becomeLeader node transport

        let newPeer =
            { Id = 4
              Host = "127.0.0.1"
              Port = 5004 }

        let result = node.AddPeer newPeer
        Assert.True result

        let state = node.GetState()
        Assert.Contains(state.NonVotingPeers, fun p -> p.Id = 4)
    }

[<Fact>]
let ``RaftNode.AddPeer returns false when not leader`` () =
    task {
        let node, _, _ = makeNode (configWithPeers 1 0)

        let newPeer =
            { Id = 4
              Host = "127.0.0.1"
              Port = 5004 }

        let result = node.AddPeer newPeer
        Assert.False result
    }

[<Fact>]
let ``RaftNode.RemovePeer returns true when leader`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let _ = becomeLeader node transport
        let result = node.RemovePeer 2
        Assert.True result
    }

[<Fact>]
let ``RaftNode.RemovePeer returns false when not leader`` () =
    task {
        let node, _, _ = makeNode (configWithPeers 1 0)
        let result = node.RemovePeer 2
        Assert.False result
    }

[<Fact>]
let ``RaftNode handles RequestVote and wins election to become Leader`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)

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
        let finalState = node.GetState()
        Assert.Equal(Follower, finalState.Role)
    }

[<Fact>]
let ``RaftNode.Dispose does not throw`` () =
    task {
        let node, _, _ = makeNode (configForNode 1 0)

        let ex: System.Exception option =
            try
                (node :> System.IDisposable).Dispose()
                None
            with e ->
                Some e

        Assert.Null ex
    }
