module Raft.Tests.ReplicationTests

open Xunit
open Raft
open TestHelpers

let dummyEntry =
    { Index = 1L
      Term = 1L
      Command = "put x 1"
      ClientId = None
      SeqNum = None }

[<Fact>]
let ``Replication.createAppendEntries returns None when node is not Leader`` () =
    let state = State.init dummyConfig None
    Assert.True(Replication.createAppendEntries 2 state |> Option.isNone)
    Assert.True(Replication.createHeartbeat 2 state |> Option.isNone)

[<Fact>]
let ``Replication.handleAppendEntries rejects request when leader term is lower than follower term`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty } }

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
    Assert.Equal(0, newState.Persistent.Log.Count)

[<Fact>]
let ``Replication.handleAppendEntries appends entries and updates commit index on success`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty } }

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
    Assert.Equal(1, newState.Persistent.Log.Count)
    Assert.Equal(1L, newState.Volatile.CommitIndex)

[<Fact>]
let ``Replication.handleAppendEntries rejects when PrevLogIndex term mismatches local log`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = logFromList [ dummyEntry ]
                  Snapshot = None
                  SessionTable = Map.empty } }

    let ae =
        { LeaderTerm = 2L
          LeaderId = 2
          PrevLogIndex = 1L
          PrevLogTerm = 2L
          Entries = []
          LeaderCommit = 0L }

    let _, resp = Replication.handleAppendEntries ae state
    Assert.False resp.Success
    Assert.Equal(0L, resp.MatchIndex)

[<Fact>]
let ``Replication.handleAppendEntries rejects when PrevLogIndex exceeds follower log length`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log =
                    logFromList
                        [ { Index = 1L
                            Term = 1L
                            Command = "x"
                            ClientId = None
                            SeqNum = None } ]
                  Snapshot = None
                  SessionTable = Map.empty } }

    let ae =
        { LeaderTerm = 2L
          LeaderId = 2
          PrevLogIndex = 5L
          PrevLogTerm = 2L
          Entries = []
          LeaderCommit = 0L }

    let _, resp = Replication.handleAppendEntries ae state
    Assert.False resp.Success
    Assert.Equal(0L, resp.ConflictTerm)
    Assert.Equal(2L, resp.ConflictIndex)

[<Fact>]
let ``Replication.handleAppendEntries with PrevLogIndex > log last index returns ConflictIndex = lastIndex + 1`` () =
    let log =
        logFromList
            [ { Index = 1L
                Term = 1L
                Command = "a"
                ClientId = None
                SeqNum = None } ]

    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = log
                  Snapshot = None
                  SessionTable = Map.empty }
            CurrentLeader = Some 2 }

    let ae =
        { LeaderTerm = 1L
          LeaderId = 2
          PrevLogIndex = 5L
          PrevLogTerm = 0L
          Entries = []
          LeaderCommit = 0L }

    let _, resp = Replication.handleAppendEntries ae state
    Assert.False resp.Success
    Assert.Equal(0L, resp.ConflictTerm)
    Assert.Equal(2L, resp.ConflictIndex)

[<Fact>]
let ``Replication.handleAppendEntries with conflict at log index 1 triggers firstIdx base case`` () =
    let log =
        logFromList
            [ { Index = 1L
                Term = 1L
                Command = "a"
                ClientId = None
                SeqNum = None } ]

    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = log
                  Snapshot = None
                  SessionTable = Map.empty }
            CurrentLeader = Some 3 }

    let ae =
        { LeaderTerm = 2L
          LeaderId = 3
          PrevLogIndex = 1L
          PrevLogTerm = 2L
          Entries = []
          LeaderCommit = 0L }

    let _, resp = Replication.handleAppendEntries ae state
    Assert.False resp.Success
    Assert.Equal(1L, resp.ConflictTerm)
    Assert.Equal(1L, resp.ConflictIndex)

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
                  Log = logFromList [ dummyEntry ]
                  Snapshot = None
                  SessionTable = Map.empty }
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
let ``Replication.handleAppendEntriesResponse uses ConflictTerm optimization when leader has entries in that term`` () =
    let leaderState =
        { NextIndex = Map.ofList [ 2, 4L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log =
                    logFromList
                        [ { Index = 1L
                            Term = 1L
                            Command = "x"
                            ClientId = None
                            SeqNum = None }
                          { Index = 2L
                            Term = 1L
                            Command = "y"
                            ClientId = None
                            SeqNum = None }
                          { Index = 3L
                            Term = 1L
                            Command = "z"
                            ClientId = None
                            SeqNum = None } ]
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some leaderState }

    let resp =
        { FollowerTerm = 1L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 1L
          ConflictIndex = 2L }

    let newState = Replication.handleAppendEntriesResponse resp state
    Assert.Equal(3L, newState.LeaderState.Value.NextIndex.[2])

[<Fact>]
let ``Replication.handleAppendEntriesResponse uses ConflictIndex when leader has no entries in ConflictTerm`` () =
    let leaderState =
        { NextIndex = Map.ofList [ 2, 4L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log =
                    logFromList
                        [ { Index = 1L
                            Term = 1L
                            Command = "x"
                            ClientId = None
                            SeqNum = None } ]
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some leaderState }

    let resp =
        { FollowerTerm = 2L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 2L
          ConflictIndex = 3L }

    let newState = Replication.handleAppendEntriesResponse resp state
    Assert.Equal(3L, newState.LeaderState.Value.NextIndex.[2])

[<Fact>]
let ``Replication.handleAppendEntriesResponse with success updates match and next indices`` () =
    let ls: LeaderState =
        { NextIndex = Map.ofList [ 2, 5L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some ls }

    let resp =
        { FollowerTerm = 1L
          Success = true
          MatchIndex = 4L
          FollowerId = 2
          ConflictTerm = 0L
          ConflictIndex = 0L }

    let newState = Replication.handleAppendEntriesResponse resp state
    Assert.Equal(4L, newState.LeaderState.Value.MatchIndex.[2])
    Assert.Equal(5L, newState.LeaderState.Value.NextIndex.[2])

[<Fact>]
let ``Replication.handleAppendEntriesResponse with ConflictTerm decrements NextIndex appropriately`` () =
    let log =
        logFromList
            [ { Index = 3L
                Term = 1L
                Command = "x"
                ClientId = None
                SeqNum = None } ]

    let ls: LeaderState =
        { NextIndex = Map.ofList [ 2, 5L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = log
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some ls }

    let resp =
        { FollowerTerm = 1L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 1L
          ConflictIndex = 4L }

    let newState = Replication.handleAppendEntriesResponse resp state
    Assert.Equal(3L, newState.LeaderState.Value.NextIndex.[2])

[<Fact>]
let ``Replication.handleAppendEntriesResponse with ConflictTerm not found in log uses ConflictIndex`` () =
    let log =
        logFromList
            [ { Index = 1L
                Term = 1L
                Command = "a"
                ClientId = None
                SeqNum = None } ]

    let ls: LeaderState =
        { NextIndex = Map.ofList [ 2, 5L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = log
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some ls }

    let resp =
        { FollowerTerm = 2L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 99L
          ConflictIndex = 3L }

    let newState = Replication.handleAppendEntriesResponse resp state
    Assert.Equal(3L, newState.LeaderState.Value.NextIndex.[2])

[<Fact>]
let ``Replication.handleAppendEntriesResponse with ConflictTerm=0 uses ConflictIndex`` () =
    let ls: LeaderState =
        { NextIndex = Map.ofList [ 2, 5L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some ls }

    let resp =
        { FollowerTerm = 1L
          Success = false
          MatchIndex = 0L
          FollowerId = 2
          ConflictTerm = 0L
          ConflictIndex = 3L }

    let newState = Replication.handleAppendEntriesResponse resp state
    Assert.Equal(3L, newState.LeaderState.Value.NextIndex.[2])

[<Fact>]
let ``Replication.createInstallSnapshot returns None when node is not Leader`` () =
    let state = State.init dummyConfig None
    Assert.True(Replication.createInstallSnapshot 2 state |> Option.isNone)

[<Fact>]
let ``Replication.createInstallSnapshot returns None when no snapshot exists`` () =
    let state = State.initLeaderState (State.init dummyConfig None)
    Assert.True(Replication.createInstallSnapshot 2 state |> Option.isNone)

[<Fact>]
let ``Replication.createInstallSnapshot returns Some when snapshot exists and follower needs it`` () =
    let log =
        logFromList
            [ { Index = 4L
                Term = 1L
                Command = "c"
                ClientId = None
                SeqNum = None } ]

    let ls: LeaderState =
        { NextIndex = Map.ofList [ 2, 1L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = log
                  Snapshot =
                    Some
                        { LastIncludedIndex = 3L
                          LastIncludedTerm = 1L
                          StateMachineData = "snap" }
                  SessionTable = Map.empty }
            LeaderState = Some ls }

    let snap = Replication.createInstallSnapshot 2 state
    Assert.True snap.IsSome
    Assert.Equal(3L, snap.Value.LastIncludedIndex)
    Assert.Equal(1L, snap.Value.LastIncludedTerm)
    Assert.Equal("snap", snap.Value.Data)

[<Fact>]
let ``Replication.createInstallSnapshot returns None when snapshot is behind follower's next index`` () =
    let log =
        logFromList
            [ { Index = 5L
                Term = 1L
                Command = "c"
                ClientId = None
                SeqNum = None } ]

    let ls: LeaderState =
        { NextIndex = Map.ofList [ 2, 10L ]
          MatchIndex = Map.ofList [ 2, 9L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log = log
                  Snapshot =
                    Some
                        { LastIncludedIndex = 5L
                          LastIncludedTerm = 1L
                          StateMachineData = "snap" }
                  SessionTable = Map.empty }
            LeaderState = Some ls }

    let snap = Replication.createInstallSnapshot 2 state
    Assert.True snap.IsNone

[<Fact>]
let ``Replication.createInstallSnapshot returns None when there is no snapshot`` () =
    let state = State.initLeaderState (State.init dummyConfig None)
    let snap = Replication.createInstallSnapshot 2 state
    Assert.True snap.IsNone

[<Fact>]
let ``Replication.handleInstallSnapshot rejects when leader term is lower`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 3L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty } }

    let snap =
        { LeaderTerm = 2L
          LeaderId = 5
          LastIncludedIndex = 10L
          LastIncludedTerm = 2L
          Data = "snap-data" }

    let newState, resp = Replication.handleInstallSnapshot snap state
    Assert.False resp.Success
    Assert.Equal(0L, resp.LastIncludedIndex)
    Assert.Equal(Follower, newState.Role)

[<Fact>]
let ``Replication.handleInstallSnapshot installs snapshot and trims log`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log =
                    logFromList
                        [ { Index = 1L
                            Term = 1L
                            Command = "a"
                            ClientId = None
                            SeqNum = None }
                          { Index = 2L
                            Term = 1L
                            Command = "b"
                            ClientId = None
                            SeqNum = None }
                          { Index = 3L
                            Term = 1L
                            Command = "c"
                            ClientId = None
                            SeqNum = None } ]
                  Snapshot = None
                  SessionTable = Map.empty } }

    let snap =
        { LeaderTerm = 2L
          LeaderId = 3
          LastIncludedIndex = 2L
          LastIncludedTerm = 1L
          Data = "snap-data" }

    let newState, resp = Replication.handleInstallSnapshot snap state

    Assert.True resp.Success
    Assert.Equal(2L, resp.LastIncludedIndex)
    Assert.Equal(2L, newState.Persistent.CurrentTerm)
    Assert.Equal(Some 3, newState.CurrentLeader)

    Assert.Equal(2, newState.Persistent.Log.Count)
    Assert.True(newState.Persistent.Log.ContainsKey 2L)
    Assert.True(newState.Persistent.Log.ContainsKey 3L)
    Assert.Equal("", newState.Persistent.Log.[2L].Command)
    Assert.Equal("c", newState.Persistent.Log.[3L].Command)

    Assert.True newState.Persistent.Snapshot.IsSome
    Assert.Equal(2L, newState.Persistent.Snapshot.Value.LastIncludedIndex)
    Assert.Equal("snap-data", newState.Persistent.Snapshot.Value.StateMachineData)

    Assert.Equal(2L, newState.Volatile.CommitIndex)
    Assert.Equal(2L, newState.Volatile.LastApplied)

[<Fact>]
let ``Replication.handleInstallSnapshotResponse updates match and next index on success`` () =
    let ls: LeaderState =
        { NextIndex = Map.ofList [ 2, 1L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some ls }

    let resp =
        { FollowerTerm = 2L
          FollowerId = 2
          Success = true
          LastIncludedIndex = 10L }

    let newState = Replication.handleInstallSnapshotResponse resp state

    Assert.Equal(10L, newState.LeaderState.Value.MatchIndex.[2])
    Assert.Equal(11L, newState.LeaderState.Value.NextIndex.[2])

[<Fact>]
let ``Replication.handleInstallSnapshotResponse updates term when response has higher term`` () =
    let state = State.init dummyConfig None

    let resp =
        { FollowerTerm = 99L
          FollowerId = 2
          Success = false
          LastIncludedIndex = 0L }

    let newState = Replication.handleInstallSnapshotResponse resp state
    Assert.Equal(99L, newState.Persistent.CurrentTerm)

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
                  Log = logFromList [ dummyEntry ]
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some leaderState
            Volatile = { CommitIndex = 0L; LastApplied = 0L } }

    let newState = Replication.advanceCommitIndex state
    Assert.Equal(1L, newState.Volatile.CommitIndex)

[<Fact>]
let ``Replication.advanceCommitIndex returns unchanged state when node is not Leader`` () =
    let state = State.init dummyConfig None
    let newState = Replication.advanceCommitIndex state
    Assert.Equal(state, newState)

[<Fact>]
let ``Replication.advanceCommitIndex does not advance when term does not match current term`` () =
    let leaderState =
        { NextIndex = Map.ofList [ 2, 3L; 3, 3L ]
          MatchIndex = Map.ofList [ 2, 2L; 3, 2L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log =
                    logFromList
                        [ { Index = 1L
                            Term = 1L
                            Command = "a"
                            ClientId = None
                            SeqNum = None }
                          { Index = 2L
                            Term = 1L
                            Command = "b"
                            ClientId = None
                            SeqNum = None } ]
                  Snapshot = None
                  SessionTable = Map.empty }
            LeaderState = Some leaderState
            Volatile = { CommitIndex = 0L; LastApplied = 0L } }

    let newState = Replication.advanceCommitIndex state
    Assert.Equal(0L, newState.Volatile.CommitIndex)

[<Fact>]
let ``Replication.appendJointConsensus appends config entry when Leader`` () =
    let state = State.initLeaderState (State.init dummyConfig None)
    let oldPeers = dummyConfig.Peers
    let newPeers = [ { Id = 4; Host = ""; Port = 0 }; { Id = 5; Host = ""; Port = 0 } ]
    let newState = Replication.appendJointConsensus oldPeers newPeers state

    Assert.Equal(2, newState.Persistent.Log.Count)
    let entry = Map.find 2L newState.Persistent.Log
    Assert.StartsWith(ConfigChange.ConfigCommandPrefix, entry.Command)
    Assert.True(entry.Command.Contains("j"))

[<Fact>]
let ``Replication.appendJointConsensus is no-op when not Leader`` () =
    let state = State.init dummyConfig None
    let newState = Replication.appendJointConsensus [] [] state
    Assert.True(Map.isEmpty newState.Persistent.Log)

[<Fact>]
let ``Replication.appendFinalConfiguration appends final config entry when Leader`` () =
    let state = State.initLeaderState (State.init dummyConfig None)
    let newPeers = [ { Id = 4; Host = ""; Port = 0 } ]
    let newState = Replication.appendFinalConfiguration newPeers state

    Assert.Equal(2, newState.Persistent.Log.Count)
    let entry = Map.find 2L newState.Persistent.Log
    Assert.StartsWith(ConfigChange.ConfigCommandPrefix, entry.Command)
    Assert.True(entry.Command.Contains("f"))

[<Fact>]
let ``Replication.appendFinalConfiguration is no-op when not Leader`` () =
    let state = State.init dummyConfig None
    let newState = Replication.appendFinalConfiguration [] state
    Assert.True(Map.isEmpty newState.Persistent.Log)

[<Fact>]
let ``Replication.appendCommand discards command when node is not Leader`` () =
    let state = State.init dummyConfig None
    let newState = Replication.appendCommand "should fail" state
    Assert.True(Map.isEmpty newState.Persistent.Log)

[<Fact>]
let ``Replication.appendCommand appends entry when node is Leader`` () =
    let state = State.initLeaderState (State.init dummyConfig None)
    let newState = Replication.appendCommand "put x 42" state

    Assert.Equal(2, newState.Persistent.Log.Count)
    Assert.Equal(2L, (Map.find 2L newState.Persistent.Log).Index)
    Assert.Equal(0L, (Map.find 2L newState.Persistent.Log).Term)
    Assert.Equal("put x 42", (Map.find 2L newState.Persistent.Log).Command)

[<Fact>]
let ``Replication.appendCommandWithSession discards when node is not Leader`` () =
    let state = State.init dummyConfig None
    let newState = Replication.appendCommandWithSession "cmd" "client-1" 1L state
    Assert.True(Map.isEmpty newState.Persistent.Log)

[<Fact>]
let ``Replication.appendCommandWithSession appends with session info when Leader`` () =
    let state = State.initLeaderState (State.init dummyConfig None)
    let newState = Replication.appendCommandWithSession "put x 1" "client-1" 42L state

    Assert.Equal(2, newState.Persistent.Log.Count)
    let entry = Map.find 2L newState.Persistent.Log
    Assert.Equal("put x 1", entry.Command)
    Assert.Equal(Some "client-1", entry.ClientId)
    Assert.Equal(Some 42L, entry.SeqNum)

[<Fact>]
let ``Replication.appendCommandWithSession without session info appends regular entry`` () =
    let state = State.initLeaderState (State.init dummyConfig None)
    let newState = Replication.appendCommandWithSession "cmd" "" 0L state

    Assert.Equal(2, newState.Persistent.Log.Count)
    let entry = Map.find 2L newState.Persistent.Log
    Assert.Equal("cmd", entry.Command)
    Assert.Equal(Some "", entry.ClientId)
    Assert.Equal(Some 0L, entry.SeqNum)
