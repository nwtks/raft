module Raft.Tests.StateTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``State.recoverConfigPhase detects JointPhase from log`` () =
    let oldPeers = dummyConfig.Peers
    let newPeers = [ { Id = 4; Host = ""; Port = 0 }; { Id = 5; Host = ""; Port = 0 } ]
    let serialized = ConfigChange.serialize (JointChange(oldPeers, newPeers))
    let command = ConfigChange.ConfigCommandPrefix + serialized

    let log =
        logFromList
            [ { Index = 1L
                Term = 1L
                Command = command
                ClientId = None
                SeqNum = None } ]

    let phase, updatedConfig = State.recoverConfigPhase log dummyConfig 1L

    match phase with
    | JointPhase(_, np) ->
        Assert.Equal(2, np.Length)
        Assert.Equal(4, updatedConfig.Peers.Length)
    | _ -> Assert.Fail "Expected JointPhase"

[<Fact>]
let ``State.recoverConfigPhase detects FinalChange from log`` () =
    let newPeers = [ { Id = 4; Host = ""; Port = 0 }; { Id = 5; Host = ""; Port = 0 } ]
    let serialized = ConfigChange.serialize (FinalChange newPeers)
    let command = ConfigChange.ConfigCommandPrefix + serialized

    let log =
        logFromList
            [ { Index = 1L
                Term = 1L
                Command = command
                ClientId = None
                SeqNum = None } ]

    let phase, updatedConfig = State.recoverConfigPhase log dummyConfig 1L

    Assert.Equal(SinglePhase, phase)
    Assert.Equal(2, updatedConfig.Peers.Length)
    Assert.Equal(4, updatedConfig.Peers.[0].Id)

[<Fact>]
let ``State.recoverConfigPhase returns SinglePhase when no config entries in log`` () =
    let log = logFromList []
    let phase, updatedConfig = State.recoverConfigPhase log dummyConfig 0L

    Assert.Equal(SinglePhase, phase)
    Assert.Equal(dummyConfig.Peers.Length, updatedConfig.Peers.Length)

[<Fact>]
let ``State.recoverConfigPhase picks latest config change from multiple entries`` () =
    let finalPeers = [ { Id = 6; Host = ""; Port = 0 } ]

    let finalCmd =
        ConfigChange.ConfigCommandPrefix
        + ConfigChange.serialize (FinalChange finalPeers)

    let oldPeers = dummyConfig.Peers

    let jointCmd =
        ConfigChange.ConfigCommandPrefix
        + ConfigChange.serialize (JointChange(oldPeers, [ { Id = 4; Host = ""; Port = 0 } ]))

    let log =
        logFromList
            [ { Index = 1L
                Term = 1L
                Command = jointCmd
                ClientId = None
                SeqNum = None }
              { Index = 2L
                Term = 1L
                Command = finalCmd
                ClientId = None
                SeqNum = None } ]

    let phase, updatedConfig = State.recoverConfigPhase log dummyConfig 2L

    Assert.Equal(SinglePhase, phase)
    Assert.Equal(1, updatedConfig.Peers.Length)
    Assert.Equal(6, updatedConfig.Peers.[0].Id)

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
          Log =
            logFromList
                [ { Index = 1L
                    Term = 4L
                    Command = "x"
                    ClientId = None
                    SeqNum = None } ]
          Snapshot = None
          SessionTable = Map.empty
          LastConfigIndex = 0L }

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
            Term = 2L
            Command = "c"
            ClientId = None
            SeqNum = None } ]

    let state =
        { State.init dummyConfig None with
            Volatile = { CommitIndex = 3L; LastApplied = 3L }
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = logFromList entries
                  Snapshot = None
                  SessionTable = Map.empty
                  LastConfigIndex = 0L } }

    let snapped = State.takeSnapshot 2L 1L "state-data" state

    Assert.Equal(2, snapped.Persistent.Log.Count)
    Assert.True(snapped.Persistent.Log.ContainsKey 2L)
    Assert.True(snapped.Persistent.Log.ContainsKey 3L)
    Assert.Equal(Log.NoOpCommand, snapped.Persistent.Log.[2L].Command)
    Assert.Equal("c", snapped.Persistent.Log.[3L].Command)

    Assert.True snapped.Persistent.Snapshot.IsSome
    Assert.Equal(2L, snapped.Persistent.Snapshot.Value.LastIncludedIndex)
    Assert.Equal(1L, snapped.Persistent.Snapshot.Value.LastIncludedTerm)
    Assert.Equal("state-data", snapped.Persistent.Snapshot.Value.StateMachineData)

    Assert.Equal(3L, snapped.Volatile.CommitIndex)
    Assert.Equal(3L, snapped.Volatile.LastApplied)

[<Fact>]
let ``State.takeSnapshot at index 1 trims log and adds sentinel`` () =
    let baseState = State.init dummyConfig None
    let log = logFromList [ createEntry 1L 1L "a"; createEntry 2L 1L "b" ]

    let state =
        { baseState with
            Persistent = { baseState.Persistent with Log = log } }

    let snapped = State.takeSnapshot 1L 1L "snap" state
    Assert.Equal(2, snapped.Persistent.Log.Count)
    Assert.True(snapped.Persistent.Log.ContainsKey 1L)
    Assert.Equal(Log.NoOpCommand, snapped.Persistent.Log.[1L].Command)
    Assert.True(snapped.Persistent.Log.ContainsKey 2L)
    Assert.Equal("b", snapped.Persistent.Log.[2L].Command)

[<Fact>]
let ``State.takeSnapshot at last index trims all entries`` () =
    let baseState = State.init dummyConfig None
    let log = logFromList [ createEntry 1L 1L "a"; createEntry 2L 1L "b" ]

    let state =
        { baseState with
            Persistent = { baseState.Persistent with Log = log } }

    let snapped = State.takeSnapshot 2L 1L "snap" state
    Assert.Equal(1, snapped.Persistent.Log.Count)
    Assert.Equal(Log.NoOpCommand, snapped.Persistent.Log.[2L].Command)
    Assert.False(snapped.Persistent.Log.ContainsKey 1L)

[<Fact>]
let ``State.updateSessionTable updates session table entry`` () =
    let state = State.init dummyConfig None
    Assert.True state.Persistent.SessionTable.IsEmpty

    let updated = State.updateSessionTable "client-1" 5L state
    Assert.Equal(5L, updated.Persistent.SessionTable.["client-1"])
    Assert.Equal(1, updated.Persistent.SessionTable.Count)

    let updated2 = State.updateSessionTable "client-1" 10L updated
    Assert.Equal(10L, updated2.Persistent.SessionTable.["client-1"])
    Assert.Equal(1, updated2.Persistent.SessionTable.Count)

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
let ``State.enterJointConsensus sets JointPhase and union peers`` () =
    let state = State.init dummyConfig None
    Assert.Equal(SinglePhase, state.ConfigPhase)

    let oldPeers = dummyConfig.Peers
    let newPeers = [ { Id = 4; Host = ""; Port = 0 }; { Id = 5; Host = ""; Port = 0 } ]
    let updated = State.enterJointConsensus oldPeers newPeers state

    match updated.ConfigPhase with
    | JointPhase(op, np) ->
        Assert.Equal(2, op.Length)
        Assert.Equal(2, np.Length)
    | _ -> Assert.Fail "Expected JointPhase"

    Assert.Equal(4, updated.Config.Peers.Length)

[<Fact>]
let ``State.exitJointConsensus sets SinglePhase and updates peers to new list`` () =
    let oldPeers = dummyConfig.Peers
    let newPeers = [ { Id = 4; Host = ""; Port = 0 }; { Id = 5; Host = ""; Port = 0 } ]

    let state =
        State.enterJointConsensus oldPeers newPeers (State.init dummyConfig None)

    let updated = State.exitJointConsensus newPeers state
    Assert.Equal(SinglePhase, updated.ConfigPhase)
    Assert.Equal(2, updated.Config.Peers.Length)
    Assert.Equal(4, updated.Config.Peers.[0].Id)
    Assert.Equal(5, updated.Config.Peers.[1].Id)

[<Fact>]
let ``State.exitJointConsensus keeps leader when leader remains in config`` () =
    let state = State.initLeaderState (State.init dummyConfig None)
    Assert.Equal(Leader, state.Role)

    let oldPeers = dummyConfig.Peers

    let newPeers =
        [ { Id = 1
            Host = "127.0.0.1"
            Port = 5001 }
          { Id = 4; Host = ""; Port = 0 } ]

    let jointState = State.enterJointConsensus oldPeers newPeers state
    let updated = State.exitJointConsensus newPeers jointState

    Assert.Equal(Leader, updated.Role)
    Assert.Equal(SinglePhase, updated.ConfigPhase)
    Assert.Equal(2, updated.Config.Peers.Length)

[<Fact>]
let ``State.exitJointConsensus steps down leader when leader is removed from config`` () =
    let config =
        { dummyConfig with
            Peers =
                [ { Id = 2
                    Host = "127.0.0.1"
                    Port = 5002 }
                  { Id = 3
                    Host = "127.0.0.1"
                    Port = 5003 } ] }

    let state = State.initLeaderState (State.init config None)
    Assert.Equal(Leader, state.Role)

    let oldPeers = config.Peers
    let newPeers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 4; Host = ""; Port = 0 } ]
    let jointState = State.enterJointConsensus oldPeers newPeers state
    Assert.Contains(1, jointState.Config.Peers |> List.map (fun p -> p.Id))

    let finalPeers =
        [ { Id = 2; Host = ""; Port = 0 }; { Id = 4; Host = ""; Port = 0 } ]

    let updated = State.exitJointConsensus finalPeers jointState

    Assert.Equal(Follower, updated.Role)
    Assert.Equal(SinglePhase, updated.ConfigPhase)
    Assert.Equal(2, updated.Config.Peers.Length)
    Assert.Equal(2, updated.Config.Peers.[0].Id)
    Assert.Equal(4, updated.Config.Peers.[1].Id)
    Assert.Equal(None, updated.LeaderState)
    Assert.Equal(None, updated.CurrentLeader)
    Assert.Empty updated.VotesReceived

[<Fact>]
let ``State.hasQuorumJoint requires majority in both configs`` () =
    let oldPeers = [ { Id = 2; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ]
    let newPeers = [ { Id = 4; Host = ""; Port = 0 }; { Id = 5; Host = ""; Port = 0 } ]
    let nodeId = 1

    let allSupporters = set [ 1; 2; 3; 4; 5 ]
    Assert.True(State.hasQuorumJoint allSupporters oldPeers newPeers nodeId)

    let oldOnlySupporters = set [ 1; 2 ]
    Assert.False(State.hasQuorumJoint oldOnlySupporters oldPeers newPeers nodeId)

    let selfOnly = set [ 1 ]
    Assert.False(State.hasQuorumJoint selfOnly oldPeers newPeers nodeId)

[<Fact>]
let ``State.hasQuorum checks majority during SinglePhase`` () =
    let state = State.init dummyConfig None
    Assert.Equal(SinglePhase, state.ConfigPhase)

    Assert.True(State.hasQuorum (set [ 1; 2 ]) state)
    Assert.False(State.hasQuorum (set [ 1 ]) state)

[<Fact>]
let ``State.hasQuorum checks both configs during JointPhase`` () =
    let oldPeers = dummyConfig.Peers
    let newPeers = [ { Id = 4; Host = ""; Port = 0 }; { Id = 5; Host = ""; Port = 0 } ]

    let state =
        State.enterJointConsensus oldPeers newPeers (State.init dummyConfig None)

    match state.ConfigPhase with
    | JointPhase _ -> ()
    | _ -> Assert.Fail "Expected JointPhase"

    Assert.False(State.hasQuorum (set [ 1; 2; 3 ]) state)
    Assert.True(State.hasQuorum (set [ 1; 2; 3; 4 ]) state)
    Assert.False(State.hasQuorum (set [ 1; 4; 5 ]) state)
    Assert.True(State.hasQuorum (set [ 1; 2; 3; 4; 5 ]) state)
