module Raft.Tests.NodeRaftTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``NodeRaft.handleRaftRPC handles RequestVoteMsg by granting vote to candidate with higher term`` () =
    let state = State.init dummyConfig None
    let ctx, transport = makeNodeContextWithTransport state

    let rv: RequestVote =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let result = NodeRaft.handleRaftRPC ctx (RequestVoteMsg rv)
    Assert.Equal(Follower, result.State.Role)
    Assert.Equal(1L, result.State.Persistent.CurrentTerm)
    Assert.Equal(Some 2, result.State.Persistent.VotedFor)

    let sentTo2 = transport.Messages |> Seq.tryFind (fun (p, _) -> p.Id = 2)
    Assert.True sentTo2.IsSome

    match sentTo2.Value with
    | _, RequestVoteResponseMsg resp -> Assert.True resp.VoteGranted
    | _ -> Assert.Fail "Expected RequestVoteResponseMsg"

[<Fact>]
let ``NodeRaft.handleRaftRPC rejects RequestVoteMsg from candidate with lower term`` () =
    let state = State.updateTerm 2L (State.init dummyConfig None)
    let ctx, transport = makeNodeContextWithTransport state

    let rv: RequestVote =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let result = NodeRaft.handleRaftRPC ctx (RequestVoteMsg rv)
    Assert.Equal(2L, result.State.Persistent.CurrentTerm)
    Assert.Equal(Follower, result.State.Role)

    let sentTo2 = transport.Messages |> Seq.tryFind (fun (p, _) -> p.Id = 2)
    Assert.True sentTo2.IsSome

    match sentTo2.Value with
    | _, RequestVoteResponseMsg resp -> Assert.False resp.VoteGranted
    | _ -> Assert.Fail "Expected RequestVoteResponseMsg"

[<Fact>]
let ``NodeRaft.handleRaftRPC handles RequestVoteMsg from unknown peer without sending response`` () =
    let state = State.init dummyConfig None
    let ctx, transport = makeNodeContextWithTransport state

    let rv: RequestVote =
        { CandidateTerm = 1L
          CandidateId = 99
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let result = NodeRaft.handleRaftRPC ctx (RequestVoteMsg rv)
    Assert.Equal(1L, result.State.Persistent.CurrentTerm)
    Assert.DoesNotContain(transport.Messages, fun (p, _) -> p.Id = 99)

[<Fact>]
let ``NodeRaft.handleRaftRPC promotes candidate to leader on majority RequestVoteResponse`` () =
    let state = Election.startElection (State.init dummyConfig None)
    let ctx = makeNodeContext state
    Assert.Equal(Candidate, ctx.State.Role)

    let resp: RequestVoteResponse =
        { VoterId = 2
          VoterTerm = 1L
          VoteGranted = true }

    let result = NodeRaft.handleRaftRPC ctx (RequestVoteResponseMsg resp)
    Assert.Equal(Leader, result.State.Role)
    Assert.True result.State.LeaderState.IsSome
    Assert.Equal(Some 1, result.State.CurrentLeader)
    Assert.Equal(1L, result.State.Persistent.CurrentTerm)

[<Fact>]
let ``NodeRaft.handleRaftRPC stays candidate without majority on partial RequestVoteResponse`` () =
    let config5Nodes =
        { dummyConfig with
            Peers =
                [ { Id = 2; Host = ""; Port = 0 }
                  { Id = 3; Host = ""; Port = 0 }
                  { Id = 4; Host = ""; Port = 0 }
                  { Id = 5; Host = ""; Port = 0 } ] }

    let state = Election.startElection (State.init config5Nodes None)
    let ctx = makeNodeContext state

    let resp: RequestVoteResponse =
        { VoterId = 2
          VoterTerm = 1L
          VoteGranted = true }

    let result = NodeRaft.handleRaftRPC ctx (RequestVoteResponseMsg resp)
    Assert.Equal(Candidate, result.State.Role)
    Assert.Equal(2, result.State.VotesReceived.Count)

[<Fact>]
let ``NodeRaft.handleRaftRPC ignores duplicate RequestVoteResponse from same peer`` () =
    let state = Election.startElection (State.init dummyConfig None)
    let ctx = makeNodeContext state

    let resp: RequestVoteResponse =
        { VoterId = 2
          VoterTerm = 1L
          VoteGranted = true }

    let afterFirst = NodeRaft.handleRaftRPC ctx (RequestVoteResponseMsg resp)
    Assert.Equal(Leader, afterFirst.State.Role)

    let ctx2 = { ctx with State = afterFirst.State }
    let afterDup = NodeRaft.handleRaftRPC ctx2 (RequestVoteResponseMsg resp)
    Assert.Equal(Leader, afterDup.State.Role)

[<Fact>]
let ``NodeRaft.handleRaftRPC steps down on RequestVoteResponse with higher term`` () =
    let state = Election.startElection (State.init dummyConfig None)
    let ctx = makeNodeContext state

    let resp: RequestVoteResponse =
        { VoterId = 2
          VoterTerm = 2L
          VoteGranted = false }

    let result = NodeRaft.handleRaftRPC ctx (RequestVoteResponseMsg resp)
    Assert.Equal(Follower, result.State.Role)
    Assert.Equal(2L, result.State.Persistent.CurrentTerm)
    Assert.True result.State.LeaderState.IsNone

[<Fact>]
let ``NodeRaft.handleRaftRPC handles AppendEntriesMsg from higher term leader`` () =
    let state = State.init dummyConfig None
    let ctx, transport = makeNodeContextWithTransport state

    let ae: AppendEntries =
        { LeaderTerm = 1L
          LeaderId = 2
          PrevLogIndex = 0L
          PrevLogTerm = 0L
          Entries = []
          LeaderCommit = 0L }

    let result = NodeRaft.handleRaftRPC ctx (AppendEntriesMsg ae)
    Assert.Equal(Follower, result.State.Role)
    Assert.Equal(1L, result.State.Persistent.CurrentTerm)
    Assert.Equal(Some 2, result.State.CurrentLeader)

    let sentTo2 = transport.Messages |> Seq.tryFind (fun (p, _) -> p.Id = 2)
    Assert.True sentTo2.IsSome

    match sentTo2.Value with
    | _, AppendEntriesResponseMsg resp ->
        Assert.True resp.Success
        Assert.Equal(1L, resp.FollowerTerm)
    | _ -> Assert.Fail "Expected AppendEntriesResponseMsg"

[<Fact>]
let ``NodeRaft.handleRaftRPC handles AppendEntriesMsg with new log entries`` () =
    let state = State.init dummyConfig None
    let ctx, transport = makeNodeContextWithTransport state

    let ae: AppendEntries =
        { LeaderTerm = 1L
          LeaderId = 2
          PrevLogIndex = 0L
          PrevLogTerm = 0L
          Entries =
            [ { Index = 1L
                Term = 1L
                Command = "put x 1"
                ClientId = None
                SeqNum = None } ]
          LeaderCommit = 0L }

    let result = NodeRaft.handleRaftRPC ctx (AppendEntriesMsg ae)
    Assert.Equal(Follower, result.State.Role)
    Assert.Equal(1L, result.State.Persistent.CurrentTerm)
    Assert.Equal(1L, result.State.Persistent.Log.Count)
    Assert.Equal("put x 1", (Map.find 1L result.State.Persistent.Log).Command)

    match transport.Messages |> Seq.tryFind (fun (p, _) -> p.Id = 2) with
    | Some(_, AppendEntriesResponseMsg resp) ->
        Assert.True resp.Success
        Assert.Equal(1L, resp.MatchIndex)
    | _ -> Assert.Fail "Expected successful AppendEntriesResponseMsg"

[<Fact>]
let ``NodeRaft.handleRaftRPC rejects AppendEntriesMsg from stale term leader`` () =
    let state = State.updateTerm 5L (State.init dummyConfig None)
    let ctx, transport = makeNodeContextWithTransport state

    let ae: AppendEntries =
        { LeaderTerm = 3L
          LeaderId = 2
          PrevLogIndex = 0L
          PrevLogTerm = 0L
          Entries = []
          LeaderCommit = 0L }

    let result = NodeRaft.handleRaftRPC ctx (AppendEntriesMsg ae)
    Assert.Equal(5L, result.State.Persistent.CurrentTerm)

    match transport.Messages |> Seq.tryFind (fun (p, _) -> p.Id = 2) with
    | Some(_, AppendEntriesResponseMsg resp) ->
        Assert.False resp.Success
        Assert.Equal(5L, resp.FollowerTerm)
    | _ -> Assert.Fail "Expected rejected AppendEntriesResponseMsg"

[<Fact>]
let ``NodeRaft.handleRaftRPC handles AppendEntriesMsg from unknown leader without sending response`` () =
    let state = State.init dummyConfig None
    let ctx, transport = makeNodeContextWithTransport state

    let ae: AppendEntries =
        { LeaderTerm = 1L
          LeaderId = 99
          PrevLogIndex = 0L
          PrevLogTerm = 0L
          Entries = []
          LeaderCommit = 0L }

    let _ = NodeRaft.handleRaftRPC ctx (AppendEntriesMsg ae)
    Assert.DoesNotContain(transport.Messages, fun (p, _) -> p.Id = 99)

[<Fact>]
let ``NodeRaft.handleRaftRPC updates match index on successful AppendEntriesResponse`` () =
    let state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    let ctx = makeNodeContext state

    let resp: AppendEntriesResponse =
        { FollowerTerm = 1L
          Success = true
          MatchIndex = 1L
          FollowerId = 2
          ConflictTerm = 0L
          ConflictIndex = 0L }

    let result = NodeRaft.handleRaftRPC ctx (AppendEntriesResponseMsg resp)
    Assert.Equal(Leader, result.State.Role)

    match result.State.LeaderState with
    | Some ls ->
        Assert.Equal(Some 2L, ls.NextIndex.TryFind 2)
        Assert.Equal(Some 1L, ls.MatchIndex.TryFind 2)
    | None -> Assert.Fail "Expected LeaderState"

[<Fact>]
let ``NodeRaft.handleRaftRPC steps down on AppendEntriesResponse with higher term`` () =
    let state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    let ctx = makeNodeContext state

    let resp: AppendEntriesResponse =
        { FollowerTerm = 2L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 0L
          ConflictIndex = 0L }

    let result = NodeRaft.handleRaftRPC ctx (AppendEntriesResponseMsg resp)
    Assert.Equal(Follower, result.State.Role)
    Assert.Equal(2L, result.State.Persistent.CurrentTerm)
    Assert.True result.State.LeaderState.IsNone

[<Fact>]
let ``NodeRaft.handleRaftRPC handles AppendEntriesResponse with conflict term and decrements NextIndex`` () =
    let mutable state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    state <- Replication.appendCommand "cmd1" state
    state <- Replication.appendCommand "cmd2" state

    state <-
        State.updateLeaderState
            (state.LeaderState.Value.NextIndex |> Map.add 2 4L)
            state.LeaderState.Value.MatchIndex
            state

    let ctx = makeNodeContext state

    let resp: AppendEntriesResponse =
        { FollowerTerm = 1L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 2L
          ConflictIndex = 2L }

    let result = NodeRaft.handleRaftRPC ctx (AppendEntriesResponseMsg resp)
    Assert.Equal(Leader, result.State.Role)

    match result.State.LeaderState with
    | Some ls ->
        let nextIdx = ls.NextIndex.TryFind 2 |> Option.defaultValue 0L
        Assert.Equal(2L, nextIdx)
    | None -> Assert.Fail "Expected LeaderState"

[<Fact>]
let ``NodeRaft.handleRaftRPC handles InstallSnapshotMsg from leader and installs snapshot`` () =
    let mutable installedData = ""
    let state = State.init dummyConfig None
    let ctx, transport = makeNodeContextWithTransport state

    let ctxWithCallback =
        { ctx with
            OnInstallSnapshot = fun data -> installedData <- data }

    let snap: InstallSnapshot =
        { LeaderTerm = 1L
          LeaderId = 2
          LastIncludedIndex = 5L
          LastIncludedTerm = 1L
          Data = "snapshot-data" }

    let result = NodeRaft.handleRaftRPC ctxWithCallback (InstallSnapshotMsg snap)
    Assert.Equal(Follower, result.State.Role)
    Assert.True result.State.Persistent.Snapshot.IsSome
    Assert.Equal(5L, result.State.Persistent.Snapshot.Value.LastIncludedIndex)
    Assert.Equal("snapshot-data", result.State.Persistent.Snapshot.Value.StateMachineData)

    let sentTo2 = transport.Messages |> Seq.tryFind (fun (p, _) -> p.Id = 2)
    Assert.True sentTo2.IsSome

    match sentTo2.Value with
    | _, InstallSnapshotResponseMsg resp ->
        Assert.True resp.Success
        Assert.Equal(5L, resp.LastIncludedIndex)
    | _ -> Assert.Fail "Expected InstallSnapshotResponseMsg"

[<Fact>]
let ``NodeRaft.handleRaftRPC rejects InstallSnapshotMsg from stale term leader`` () =
    let state = State.updateTerm 5L (State.init dummyConfig None)
    let ctx, transport = makeNodeContextWithTransport state

    let snap: InstallSnapshot =
        { LeaderTerm = 3L
          LeaderId = 2
          LastIncludedIndex = 10L
          LastIncludedTerm = 3L
          Data = "stale-data" }

    let result = NodeRaft.handleRaftRPC ctx (InstallSnapshotMsg snap)
    Assert.True result.State.Persistent.Snapshot.IsNone

    match transport.Messages |> Seq.tryFind (fun (p, _) -> p.Id = 2) with
    | Some(_, InstallSnapshotResponseMsg resp) ->
        Assert.False resp.Success
        Assert.Equal(5L, resp.FollowerTerm)
    | _ -> Assert.Fail "Expected unsuccessful InstallSnapshotResponseMsg"

[<Fact>]
let ``NodeRaft.handleRaftRPC handles InstallSnapshotMsg from unknown leader without sending response`` () =
    let state = State.init dummyConfig None
    let ctx, transport = makeNodeContextWithTransport state

    let snap: InstallSnapshot =
        { LeaderTerm = 1L
          LeaderId = 99
          LastIncludedIndex = 5L
          LastIncludedTerm = 1L
          Data = "data" }

    let _ = NodeRaft.handleRaftRPC ctx (InstallSnapshotMsg snap)
    Assert.DoesNotContain(transport.Messages, fun (p, _) -> p.Id = 99)

[<Fact>]
let ``NodeRaft.handleRaftRPC updates match index on successful InstallSnapshotResponse`` () =
    let state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    let ctx = makeNodeContext state

    let resp: InstallSnapshotResponse =
        { FollowerTerm = 1L
          FollowerId = 2
          Success = true
          LastIncludedIndex = 5L }

    let result = NodeRaft.handleRaftRPC ctx (InstallSnapshotResponseMsg resp)
    Assert.Equal(Leader, result.State.Role)

    match result.State.LeaderState with
    | Some ls ->
        Assert.Equal(Some 5L, ls.MatchIndex.TryFind 2)
        Assert.Equal(Some 6L, ls.NextIndex.TryFind 2)
    | None -> Assert.Fail "Expected LeaderState"

[<Fact>]
let ``NodeRaft.handleRaftRPC steps down on InstallSnapshotResponse with higher term`` () =
    let state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    let ctx = makeNodeContext state

    let resp: InstallSnapshotResponse =
        { FollowerTerm = 2L
          FollowerId = 2
          Success = false
          LastIncludedIndex = 0L }

    let result = NodeRaft.handleRaftRPC ctx (InstallSnapshotResponseMsg resp)
    Assert.Equal(Follower, result.State.Role)
    Assert.Equal(2L, result.State.Persistent.CurrentTerm)
    Assert.True result.State.LeaderState.IsNone

[<Fact>]
let ``NodeRaft.handleRaftRPC ignores InstallSnapshotResponse when not leader`` () =
    let state = State.init dummyConfig None
    let ctx = makeNodeContext state

    let resp: InstallSnapshotResponse =
        { FollowerTerm = 1L
          FollowerId = 2
          Success = true
          LastIncludedIndex = 5L }

    let result = NodeRaft.handleRaftRPC ctx (InstallSnapshotResponseMsg resp)
    Assert.Equal(Follower, result.State.Role)
    Assert.True result.State.LeaderState.IsNone

[<Fact>]
let ``NodeRaft.handleRaftRPC returns Reset election timer on RequestVoteMsg from higher term`` () =
    let state = State.init dummyConfig None
    let ctx = makeNodeContext state

    let rv: RequestVote =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let result = NodeRaft.handleRaftRPC ctx (RequestVoteMsg rv)
    Assert.Equal(Reset, result.ElectionAction)
    Assert.Equal(Keep, result.HeartbeatAction)
