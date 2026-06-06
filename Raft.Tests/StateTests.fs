module Raft.Tests.StateTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``State.init without persisted state creates default PersistentState with term 0`` () =
    let state = State.init dummyConfigStandalone None

    Assert.Equal(Follower, state.Role)
    Assert.Equal(0L, state.Persistent.CurrentTerm)
    Assert.Equal(None, state.Persistent.VotedFor)
    Assert.Empty state.Persistent.Log
    Assert.Equal(0L, state.Volatile.CommitIndex)
    Assert.Equal(0L, state.Volatile.LastApplied)

[<Fact>]
let ``State.init with persisted state restores CurrentTerm, VotedFor, and Log correctly`` () =
    let loaded: PersistentState =
        { CurrentTerm = 5L
          VotedFor = Some 2
          Log = logFromList [ { Index = 1L; Term = 4L; Command = "x" } ]
          Snapshot = None }

    let state = State.init dummyConfigStandalone (Some loaded)

    Assert.Equal(5L, state.Persistent.CurrentTerm)
    Assert.Equal(Some 2, state.Persistent.VotedFor)
    Assert.Equal(1, state.Persistent.Log.Count)
    Assert.Equal(1L, (Map.find 1L state.Persistent.Log).Index)
    Assert.Equal(Follower, state.Role)
    Assert.Equal(0L, state.Volatile.CommitIndex)
    Assert.Equal(0L, state.Volatile.LastApplied)
    Assert.True state.LeaderState.IsNone

[<Fact>]
let ``State.initLeaderState creates correct NextIndex and MatchIndex for all peers`` () =
    let state = State.init dummyConfig None
    let leaderState = State.initLeaderState state

    Assert.Equal(Leader, leaderState.Role)
    Assert.True leaderState.LeaderState.IsSome
    Assert.Equal(1, leaderState.Config.NodeId)

    let ls = leaderState.LeaderState.Value

    for p in dummyConfig.Peers do
        Assert.Equal(1L, ls.NextIndex.[p.Id])
        Assert.Equal(0L, ls.MatchIndex.[p.Id])

[<Fact>]
let ``State.updateTerm resets to Follower when new term is higher`` () =
    let state = State.init dummyConfig None
    let leader = State.initLeaderState state

    Assert.Equal(Leader, leader.Role)
    let updated = State.updateTerm 5L leader

    Assert.Equal(Follower, updated.Role)
    Assert.Equal(5L, updated.Persistent.CurrentTerm)
    Assert.Equal(None, updated.Persistent.VotedFor)
    Assert.True updated.LeaderState.IsNone
    Assert.True updated.VotesReceived.IsEmpty

[<Fact>]
let ``State.updateTerm is no-op when new term equals current term`` () =
    let state = State.init dummyConfig None
    let updated = State.updateTerm 0L state
    Assert.Equal(state, updated)

[<Fact>]
let ``State.updateLogAndCommit updates both log and commit index`` () =
    let state = State.init dummyConfig None
    let newLog = Log.append 1L "test" state.Persistent.Log

    let updated = State.updateLogAndCommit newLog 1L state

    Assert.Equal(1, updated.Persistent.Log.Count)
    Assert.Equal(1L, updated.Volatile.CommitIndex)
    Assert.Equal(0L, updated.Volatile.LastApplied)

[<Fact>]
let ``State.updateCommitIndex only updates commit index`` () =
    let state = State.init dummyConfig None
    let updated = State.updateCommitIndex 5L state

    Assert.Equal(5L, updated.Volatile.CommitIndex)
    Assert.Equal(0L, updated.Volatile.LastApplied)

[<Fact>]
let ``State.updateLastApplied only updates last applied`` () =
    let state = State.init dummyConfig None
    let updated = State.updateLastApplied 3L state

    Assert.Equal(3L, updated.Volatile.LastApplied)
    Assert.Equal(0L, updated.Volatile.CommitIndex)

[<Fact>]
let ``State.followLeader updates role to Follower and records leader`` () =
    let state = State.init dummyConfig None
    let updated = State.followLeader 3L 2 state

    Assert.Equal(Follower, updated.Role)
    Assert.Equal(3L, updated.Persistent.CurrentTerm)
    Assert.Equal(Some 2, updated.CurrentLeader)

[<Fact>]
let ``State.updateLeaderState updates NextIndex and MatchIndex`` () =
    let state =
        { State.init dummyConfig None with
            Role = Leader
            LeaderState =
                Some
                    { NextIndex = Map.ofList [ 2, 1L; 3, 1L ]
                      MatchIndex = Map.ofList [ 2, 0L; 3, 0L ] } }

    let updated =
        State.updateLeaderState (Map.ofList [ 2, 3L; 3, 2L ]) (Map.ofList [ 2, 2L; 3, 1L ]) state

    Assert.Equal(3L, updated.LeaderState.Value.NextIndex.[2])
    Assert.Equal(2L, updated.LeaderState.Value.MatchIndex.[2])
    Assert.Equal(2L, updated.LeaderState.Value.NextIndex.[3])
    Assert.Equal(1L, updated.LeaderState.Value.MatchIndex.[3])

[<Fact>]
let ``State.updateLeaderState is no-op when LeaderState is None`` () =
    let state = State.init dummyConfig None
    Assert.True state.LeaderState.IsNone

    let updated =
        State.updateLeaderState (Map.ofList [ 2, 1L ]) (Map.ofList [ 2, 0L ]) state

    Assert.Equal(state, updated)

[<Fact>]
let ``State.recordVote updates VotedFor`` () =
    let state = State.init dummyConfig None
    Assert.Equal(None, state.Persistent.VotedFor)

    let updated = State.recordVote 42 state
    Assert.Equal(Some 42, updated.Persistent.VotedFor)

[<Fact>]
let ``State.addVoteReceived adds to VotesReceived set`` () =
    let state = State.init dummyConfig None
    Assert.Empty state.VotesReceived

    let updated = State.addVoteReceived 5 state
    Assert.True(updated.VotesReceived.Contains 5)
    Assert.Equal(1, updated.VotesReceived.Count)

    let updated2 = State.addVoteReceived 5 updated
    Assert.Equal(1, updated2.VotesReceived.Count)

[<Fact>]
let ``State.takeSnapshot trims log and stores snapshot`` () =
    let entries =
        [ { Index = 1L; Term = 1L; Command = "a" }
          { Index = 2L; Term = 1L; Command = "b" }
          { Index = 3L; Term = 2L; Command = "c" } ]

    let state =
        { State.init dummyConfig None with
            Volatile = { CommitIndex = 3L; LastApplied = 3L }
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = logFromList entries
                  Snapshot = None } }

    let snapped = State.takeSnapshot 2L 1L "state-data" state

    Assert.Equal(2, snapped.Persistent.Log.Count)
    Assert.True(snapped.Persistent.Log.ContainsKey 2L)
    Assert.True(snapped.Persistent.Log.ContainsKey 3L)
    Assert.Equal("", snapped.Persistent.Log.[2L].Command)
    Assert.Equal("c", snapped.Persistent.Log.[3L].Command)

    Assert.True snapped.Persistent.Snapshot.IsSome
    Assert.Equal(2L, snapped.Persistent.Snapshot.Value.LastIncludedIndex)
    Assert.Equal(1L, snapped.Persistent.Snapshot.Value.LastIncludedTerm)
    Assert.Equal("state-data", snapped.Persistent.Snapshot.Value.StateMachineData)

    Assert.Equal(3L, snapped.Volatile.CommitIndex)
    Assert.Equal(3L, snapped.Volatile.LastApplied)

[<Fact>]
let ``State.updateConfig replaces peers list`` () =
    let state = State.init dummyConfig None
    let newPeers = [ { Id = 5; Host = ""; Port = 0 }; { Id = 6; Host = ""; Port = 0 } ]
    let updated = State.updateConfig newPeers state

    Assert.Equal(2, updated.Config.Peers.Length)
    Assert.Equal(5, updated.Config.Peers[0].Id)
    Assert.Equal(6, updated.Config.Peers[1].Id)

    Assert.Equal(2, dummyConfig.Peers.Length)

[<Fact>]
let ``State.quorumSize returns majority for 3-node cluster`` () =
    let state = State.init dummyConfig None
    Assert.Equal(2, State.quorumSize state)

[<Fact>]
let ``State.quorumSize returns majority for 1-node cluster`` () =
    let state = State.init dummyConfigStandalone None
    Assert.Equal(1, State.quorumSize state)

[<Fact>]
let ``State.quorumSize returns majority for 5-node cluster`` () =
    let config5 =
        { dummyConfig with
            Peers =
                [ { Id = 2; Host = ""; Port = 0 }
                  { Id = 3; Host = ""; Port = 0 }
                  { Id = 4; Host = ""; Port = 0 }
                  { Id = 5; Host = ""; Port = 0 } ] }

    let state = State.init config5 None
    Assert.Equal(3, State.quorumSize state)
