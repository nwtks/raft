module Raft.Tests.ReplicationTests

open Xunit
open Raft
open TestHelpers

let dummyEntry =
    { Index = 1L
      Term = 1L
      Command = "put x 1" }

[<Fact>]
let ``Replication.handleAppendEntries rejects request when leader term is lower than follower term`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = [] } }

    let ae =
        { LeaderTerm = 1L
          LeaderId = 2
          PrevLogIndex = 0L
          PrevLogTerm = 0L
          Entries = [ dummyEntry ]
          LeaderCommit = 0L }

    let newState, resp = Replication.handleAppendEntries ae state
    Assert.False resp.Success
    Assert.Equal(2L, resp.FollowerTerm)
    Assert.Equal(0, newState.Persistent.Log.Length)

[<Fact>]
let ``Replication.handleAppendEntries appends entries and updates commit index on success`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = [] } }

    let ae =
        { LeaderTerm = 1L
          LeaderId = 2
          PrevLogIndex = 0L
          PrevLogTerm = 0L
          Entries = [ dummyEntry ]
          LeaderCommit = 1L }

    let newState, resp = Replication.handleAppendEntries ae state
    Assert.True resp.Success
    Assert.Equal(1L, resp.MatchIndex)
    Assert.Equal(1, newState.Persistent.Log.Length)
    Assert.Equal(1L, newState.Volatile.CommitIndex)

[<Fact>]
let ``Replication.advanceCommitIndex advances commit index when majority of peers have matched`` () =
    let leaderState =
        { NextIndex = Map.ofList [ 2, 2L; 3, 1L ]
          MatchIndex = Map.ofList [ 2, 1L; 3, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = [ dummyEntry ] }
            LeaderState = Some leaderState
            Volatile = { CommitIndex = 0L; LastApplied = 0L } }

    let newState = Replication.advanceCommitIndex state
    Assert.Equal(1L, newState.Volatile.CommitIndex)

[<Fact>]
let ``Replication.createAppendEntries returns None when node is not Leader`` () =
    let state = State.init dummyConfig None
    Assert.True(Replication.createAppendEntries 2 state |> Option.isNone)
    Assert.True(Replication.createHeartbeat 2 state |> Option.isNone)

[<Fact>]
let ``Replication.handleAppendEntries rejects when PrevLogIndex term mismatches local log`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = [ dummyEntry ] } } // index 1, term 1

    let ae =
        { LeaderTerm = 2L
          LeaderId = 2
          PrevLogIndex = 1L
          PrevLogTerm = 2L // Mismatch! state has Term 1 at Index 1
          Entries = []
          LeaderCommit = 0L }

    let _, resp = Replication.handleAppendEntries ae state
    Assert.False resp.Success
    Assert.Equal(0L, resp.MatchIndex)

[<Fact>]
let ``Replication.handleAppendEntriesResponse updates term when response contains higher term`` () =
    let state = State.init dummyConfig None

    let resp =
        { FollowerTerm = 2L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 0L
          ConflictIndex = 0L }

    let newState = Replication.handleAppendEntriesResponse resp state
    Assert.Equal(2L, newState.Persistent.CurrentTerm)

[<Fact>]
let ``Replication.handleAppendEntriesResponse ignores response when node is not the Leader`` () =
    let state = State.init dummyConfig None

    let resp =
        { FollowerTerm = 0L
          Success = true
          MatchIndex = 1L
          FollowerId = 2
          ConflictTerm = 0L
          ConflictIndex = 0L }

    let newState = Replication.handleAppendEntriesResponse resp state
    Assert.Equal(state, newState)

[<Fact>]
let ``Replication.handleAppendEntriesResponse decrements NextIndex on failure to resolve log inconsistency`` () =
    let leaderState =
        { NextIndex = Map.ofList [ 2, 2L; 3, 1L ]
          MatchIndex = Map.ofList [ 2, 1L; 3, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = [ dummyEntry ] }
            LeaderState = Some leaderState }

    let resp =
        { FollowerTerm = 1L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 0L
          ConflictIndex = 1L }

    let newState = Replication.handleAppendEntriesResponse resp state
    let updatedNext = newState.LeaderState.Value.NextIndex.Item 2
    Assert.Equal(1L, updatedNext)

[<Fact>]
let ``Replication.advanceCommitIndex returns unchanged state when node is not Leader`` () =
    let state = State.init dummyConfig None
    let newState = Replication.advanceCommitIndex state
    Assert.Equal(state, newState)

[<Fact>]
let ``Replication.appendCommand discards command when node is not Leader`` () =
    let state = State.init dummyConfig None
    let newState = Replication.appendCommand "should fail" state
    Assert.Empty newState.Persistent.Log
