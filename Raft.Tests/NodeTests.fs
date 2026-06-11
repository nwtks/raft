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
let ``Leader RaftNode accepts submitted commands and applies them to state machine`` () =
    task {
        let applied = ResizeArray<LogEntry>()
        let node, _, _ = makeNodeWithApply (configForNode 1 0) (fun e -> applied.Add e)

        node.TriggerElectionTimeout()
        Assert.Equal(Accepted, node.SubmitCommand "put x 10")

        let finalState = node.GetState()
        Assert.Equal(2, finalState.Persistent.Log.Count)
        Assert.Equal("put x 10", (Map.find 2L finalState.Persistent.Log).Command)
    }

[<Fact>]
let ``RaftNode.SubmitCommand returns Redirect when node is not Leader`` () =
    task {
        let node, _, _ = makeNode (configForNode 1 0)

        match node.SubmitCommand "put a 1" with
        | Redirect _ -> ()
        | Accepted -> Assert.Fail "Expected redirect"
    }

[<Fact>]
let ``Leader RaftNode.SubmitCommandWithSession accepts command with clientId and seqNum`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let _ = becomeLeader node transport

        let state = node.GetState()
        Assert.Equal(Leader, state.Role)

        let result = node.SubmitCommandWithSession("put x 42", "client-1", 1L)
        Assert.Equal(Accepted, result)

        let finalState = node.GetState()
        Assert.Equal(2, finalState.Persistent.Log.Count)
        let entry = Map.find 2L finalState.Persistent.Log
        Assert.Equal(Some "client-1", entry.ClientId)
        Assert.Equal(Some 1L, entry.SeqNum)
    }

[<Fact>]
let ``RaftNode.SubmitCommandWithSession returns Accepted for duplicate session command`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let term = becomeLeader node transport
        Assert.Equal(Leader, (node.GetState()).Role)

        let result1 = node.SubmitCommandWithSession("put x 42", "client-1", 1L)
        Assert.Equal(Accepted, result1)

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

        node.GetState() |> ignore

        let result2 = node.SubmitCommandWithSession("put x 42", "client-1", 1L)
        Assert.Equal(Accepted, result2)

        let finalState = node.GetState()
        Assert.Equal(2, finalState.Persistent.Log.Count)
    }

[<Fact>]
let ``RaftNode.SubmitCommandWithSession rejects older seqNum after higher seqNum was processed`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let _ = becomeLeader node transport
        Assert.Equal(Leader, (node.GetState()).Role)
        Assert.Equal(Accepted, node.SubmitCommandWithSession("cmd", "client-1", 2L))

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = (node.GetState()).Persistent.CurrentTerm
                  Success = true
                  MatchIndex = 2L
                  FollowerId = 2
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = (node.GetState()).Persistent.CurrentTerm
                  Success = true
                  MatchIndex = 2L
                  FollowerId = 3
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        node.GetState() |> ignore

        let result = node.SubmitCommandWithSession("cmd-old", "client-1", 1L)
        Assert.Equal(Accepted, result)

        let finalState = node.GetState()
        Assert.Equal(2, finalState.Persistent.Log.Count)
    }

[<Fact>]
let ``Leader RaftNode serves LinearizableRead after committing entry in current term`` () =
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

        node.GetState() |> ignore

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
let ``Leader RaftNode queues LinearizableRead until committed entry in current term is applied`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let term = becomeLeader node transport

        let readResult = ref Unchecked.defaultof<ReadCommandResult>
        let readDone = new System.Threading.ManualResetEventSlim false

        node.PostLinearizableRead(fun r ->
            readResult.Value <- r
            readDone.Set())

        node.GetState() |> ignore

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
let ``Leader RaftNode stepping down resolves pending linearizable reads with redirect`` () =
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

        node.GetState() |> ignore

        Assert.True(readDone.Wait 5000)

        match readResult.Value with
        | ReadRedirect _ -> ()
        | ReadReady -> Assert.Fail "Expected ReadRedirect but got ReadReady"
    }

[<Fact>]
let ``Follower RaftNode LinearizableRead returns ReadRedirect None when no leader`` () =
    task {
        let node, _, _ = makeNode (configWithPeers 1 0)

        match node.LinearizableRead() with
        | ReadRedirect None -> ()
        | _ -> Assert.Fail "Expected ReadRedirect None for follower without leader"
    }

[<Fact>]
let ``Follower RaftNode LinearizableRead with known leader redirects to that leader`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)

        transport.ReceiveMessage(
            AppendEntriesMsg
                { LeaderTerm = 1L
                  LeaderId = 2
                  PrevLogIndex = 0L
                  PrevLogTerm = 0L
                  Entries = []
                  LeaderCommit = 0L }
        )

        node.GetState() |> ignore

        let state = node.GetState()
        Assert.Equal(Some 2, state.CurrentLeader)

        match node.LinearizableRead() with
        | ReadRedirect(Some peer) when peer.Id = 2 -> ()
        | other -> Assert.Fail $"Expected ReadRedirect(Some(2)) but got {other}"
    }

[<Fact>]
let ``RaftNode.SubmitTakeSnapshot creates snapshot and preserves uncommitted entries`` () =
    task {
        let node, _, _ = makeNode (configForNode 1 0)

        node.TriggerElectionTimeout()
        Assert.Equal(Leader, (node.GetState()).Role)

        Assert.Equal(Accepted, node.SubmitCommand "test-command")

        node.SubmitTakeSnapshot "snapshot-data"

        let state = node.GetState()
        Assert.True state.Persistent.Snapshot.IsSome
        Assert.Equal("snapshot-data", state.Persistent.Snapshot.Value.StateMachineData)
        Assert.True(state.Persistent.Log.ContainsKey 2L)
        Assert.Equal("test-command", state.Persistent.Log.[2L].Command)
    }

[<Fact>]
let ``RaftNode.SubmitTakeSnapshot with committed entries trims log`` () =
    task {
        let applied = ResizeArray<LogEntry>()

        let node, transport, _ =
            makeNodeWithApply (configWithPeers 1 0) (fun e -> applied.Add e)

        let term = becomeLeader node transport

        Assert.Equal(Accepted, node.SubmitCommand "cmd1")

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

        let s = node.GetState()
        Assert.Equal(2L, s.Volatile.LastApplied)
        Assert.Equal(1, applied.Count)

        Assert.Equal(Accepted, node.SubmitCommand "cmd2")
        Assert.Equal(1, applied.Count)

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 3L
                  FollowerId = 2
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        transport.ReceiveMessage(
            AppendEntriesResponseMsg
                { FollowerTerm = term
                  Success = true
                  MatchIndex = 3L
                  FollowerId = 3
                  ConflictTerm = 0L
                  ConflictIndex = 0L }
        )

        let stateWithCommitted = node.GetState()
        Assert.Equal(2, applied.Count)
        Assert.Equal(3L, stateWithCommitted.Volatile.LastApplied)

        node.SubmitTakeSnapshot "snap-data"

        let state = node.GetState()
        Assert.True state.Persistent.Snapshot.IsSome
        Assert.Equal(3L, state.Persistent.Snapshot.Value.LastIncludedIndex)
        Assert.Equal("snap-data", state.Persistent.Snapshot.Value.StateMachineData)

        Assert.False(state.Persistent.Log.ContainsKey 1L)
        Assert.False(state.Persistent.Log.ContainsKey 2L)
        Assert.True(state.Persistent.Log.ContainsKey 3L)
        Assert.Equal(Log.NoOpCommand, state.Persistent.Log.[3L].Command)
    }

[<Fact>]
let ``Leader RaftNode.AddPeer adds as non-voting member and starts replication`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let _ = becomeLeader node transport
        transport.Messages.Clear()

        let newPeer = { Id = 4; Host = "127.0.0.1"; Port = 0 }
        let success = node.AddPeer newPeer
        Assert.True success

        let finalState = node.GetState()
        Assert.Equal(1, finalState.Persistent.Log.Count)
        Assert.Contains(newPeer, finalState.NonVotingPeers)
        Assert.Contains(transport.Messages, fun (p, _) -> p.Id = 4)
    }

[<Fact>]
let ``Leader RaftNode.AddPeer returns true for already existing voting peer`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let _ = becomeLeader node transport

        let existingPeer = { Id = 2; Host = "127.0.0.1"; Port = 0 }
        let success = node.AddPeer existingPeer
        Assert.True success

        let state = node.GetState()
        Assert.Equal(Leader, state.Role)
        Assert.DoesNotContain(existingPeer, state.NonVotingPeers)
    }

[<Fact>]
let ``Non-leader RaftNode.AddPeer returns false`` () =
    task {
        let node, _, _ = makeNode (configForNode 1 0)

        let state = node.GetState()
        Assert.Equal(Follower, state.Role)

        let success = node.AddPeer { Id = 2; Host = "127.0.0.1"; Port = 0 }
        Assert.False success
    }

[<Fact>]
let ``Leader RaftNode.RemovePeer appends configuration entry and broadcasts`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let _ = becomeLeader node transport
        transport.Messages.Clear()

        let success = node.RemovePeer 3
        Assert.True success

        node.GetState() |> ignore

        let finalState = node.GetState()
        Assert.Equal(2, finalState.Persistent.Log.Count)
        Assert.StartsWith(ConfigChange.ConfigCommandPrefix, (Map.find 2L finalState.Persistent.Log).Command)

        Assert.Contains(transport.Messages, fun (p, _) -> p.Id = 2)
    }

[<Fact>]
let ``Non-leader RaftNode.RemovePeer returns false`` () =
    task {
        let node, _, _ = makeNode (configForNode 1 0)

        let state = node.GetState()
        Assert.Equal(Follower, state.Role)

        let success = node.RemovePeer 2
        Assert.False success
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

        node.GetState() |> ignore

        let finalState2 = node.GetState()
        Assert.Equal(2L, finalState2.Volatile.CommitIndex)
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

[<Fact>]
let ``RaftNode ignores RequestVote from unknown candidate`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)

        let rv =
            { CandidateTerm = 1L
              CandidateId = 99
              LastLogIndex = 0L
              LastLogTerm = 0L }

        transport.ReceiveMessage(RequestVoteMsg rv)
        node.GetState() |> ignore

        Assert.DoesNotContain(
            transport.Messages,
            fun (p, msg) ->
                p.Id = 99
                && match msg with
                   | RequestVoteResponseMsg _ -> true
                   | _ -> false
        )
    }

[<Fact>]
let ``RaftNode ignores AppendEntries from unknown leader`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)

        let ae =
            { LeaderTerm = 1L
              LeaderId = 99
              PrevLogIndex = 0L
              PrevLogTerm = 0L
              Entries = []
              LeaderCommit = 0L }

        transport.ReceiveMessage(AppendEntriesMsg ae)
        node.GetState() |> ignore

        Assert.DoesNotContain(
            transport.Messages,
            fun (p, msg) ->
                p.Id = 99
                && match msg with
                   | AppendEntriesResponseMsg _ -> true
                   | _ -> false
        )
    }

[<Fact>]
let ``RaftNode handles InstallSnapshot from known leader and installs snapshot`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)

        let snap: InstallSnapshot =
            { LeaderTerm = 1L
              LeaderId = 2
              LastIncludedIndex = 5L
              LastIncludedTerm = 1L
              Data = "snapshot-data" }

        transport.ReceiveMessage(InstallSnapshotMsg snap)
        node.GetState() |> ignore

        let state = node.GetState()
        Assert.True state.Persistent.Snapshot.IsSome
        Assert.Equal(5L, state.Persistent.Snapshot.Value.LastIncludedIndex)
        Assert.Equal("snapshot-data", state.Persistent.Snapshot.Value.StateMachineData)

        Assert.Contains(
            transport.Messages,
            fun (p, msg) ->
                p.Id = 2
                && match msg with
                   | InstallSnapshotResponseMsg _ -> true
                   | _ -> false
        )
    }

[<Fact>]
let ``Leader RaftNode handles InstallSnapshotResponse and updates match index`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)
        let term = becomeLeader node transport

        let resp: InstallSnapshotResponse =
            { FollowerTerm = term
              FollowerId = 2
              Success = true
              LastIncludedIndex = 1L }

        transport.ReceiveMessage(InstallSnapshotResponseMsg resp)
        node.GetState() |> ignore

        let state = node.GetState()
        Assert.Equal(Leader, state.Role)
        Assert.True state.LeaderState.IsSome
        Assert.Equal(Some 1L, state.LeaderState.Value.MatchIndex.TryFind 2)
        Assert.Equal(Some 2L, state.LeaderState.Value.NextIndex.TryFind 2)
    }

[<Fact>]
let ``RaftNode rejects InstallSnapshot from leader with stale term`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)

        transport.ReceiveMessage(
            AppendEntriesMsg
                { LeaderTerm = 5L
                  LeaderId = 2
                  PrevLogIndex = 0L
                  PrevLogTerm = 0L
                  Entries = []
                  LeaderCommit = 0L }
        )

        node.GetState() |> ignore

        let snap: InstallSnapshot =
            { LeaderTerm = 3L
              LeaderId = 2
              LastIncludedIndex = 10L
              LastIncludedTerm = 3L
              Data = "stale-data" }

        transport.ReceiveMessage(InstallSnapshotMsg snap)
        node.GetState() |> ignore

        let state = node.GetState()
        Assert.True state.Persistent.Snapshot.IsNone

        Assert.Contains(
            transport.Messages,
            fun (p, msg) ->
                p.Id = 2
                && match msg with
                   | InstallSnapshotResponseMsg resp -> not resp.Success
                   | _ -> false
        )
    }

[<Fact>]
let ``RaftNode ignores InstallSnapshot from unknown leader`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)

        let snap: InstallSnapshot =
            { LeaderTerm = 1L
              LeaderId = 99
              LastIncludedIndex = 5L
              LastIncludedTerm = 1L
              Data = "snapshot-data" }

        transport.ReceiveMessage(InstallSnapshotMsg snap)
        node.GetState() |> ignore

        Assert.DoesNotContain(
            transport.Messages,
            fun (p, msg) ->
                p.Id = 99
                && match msg with
                   | InstallSnapshotResponseMsg _ -> true
                   | _ -> false
        )
    }

[<Fact>]
let ``RaftNode ignores InstallSnapshotResponse when not leader`` () =
    task {
        let node, transport, _ = makeNode (configWithPeers 1 0)

        let resp: InstallSnapshotResponse =
            { FollowerTerm = 1L
              FollowerId = 2
              Success = true
              LastIncludedIndex = 5L }

        transport.ReceiveMessage(InstallSnapshotResponseMsg resp)
        node.GetState() |> ignore

        let state = node.GetState()
        Assert.Equal(Follower, state.Role)
    }
