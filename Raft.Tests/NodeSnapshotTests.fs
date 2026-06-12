module Raft.Tests.NodeSnapshotTests

open Xunit
open Raft
open TestHelpers

let makeSnapshotCtx config state =
    let inbox = new MailboxProcessor<NodeMessage>(fun _ -> async { () })

    { Config = config
      Transport = MockTransport() :> ITransport
      Persistence = MockPersistence() :> IPersistence
      OnApply = ignore
      OnInstallSnapshot = ignore
      OnGetSnapshotData = fun () -> "snap_data"
      Inbox = inbox
      State = state
      ElectionTimer = None
      HeartbeatTimer = None
      CancellationTokenSource = new System.Threading.CancellationTokenSource()
      PendingReads = [] }

[<Fact>]
let ``NodeSnapshot.handleTakeSnapshot creates snapshot at lastApplied index with given data`` () =
    let mutable state = State.init dummyConfig None
    state <- State.initLeaderState state
    state <- Replication.appendCommand "x" state
    state <- State.updateLastApplied 2L state
    let ctx = makeSnapshotCtx dummyConfig state

    let result = NodeSnapshot.handleTakeSnapshot ctx "test_data"

    Assert.True result.State.Persistent.Snapshot.IsSome
    let snap = result.State.Persistent.Snapshot.Value
    Assert.Equal(2L, snap.LastIncludedIndex)
    Assert.Equal("test_data", snap.StateMachineData)

[<Fact>]
let ``NodeSnapshot.handleTakeSnapshot trims log entries below snapshot index`` () =
    let mutable state = State.init dummyConfig None
    state <- State.initLeaderState state
    state <- Replication.appendCommand "a" state
    state <- Replication.appendCommand "b" state
    state <- State.updateLastApplied 3L state
    let ctx = makeSnapshotCtx dummyConfig state

    let result = NodeSnapshot.handleTakeSnapshot ctx "data"

    let log = result.State.Persistent.Log
    Assert.False(log |> Map.containsKey 1L)
    Assert.False(log |> Map.containsKey 2L)
    Assert.True(log |> Map.containsKey 3L)
    Assert.Equal(Log.NoOpCommand, log.[3L].Command)

[<Fact>]
let ``NodeSnapshot.handleTakeSnapshot creates snapshot and preserves uncommitted entries`` () =
    let state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    let leaderState = Replication.appendCommand "test-command" state
    let ctx = makeSnapshotCtx dummyConfig leaderState
    let result = NodeSnapshot.handleTakeSnapshot ctx "snapshot-data"
    Assert.True result.State.Persistent.Snapshot.IsSome
    Assert.Equal("snapshot-data", result.State.Persistent.Snapshot.Value.StateMachineData)
    Assert.True(result.State.Persistent.Log.ContainsKey 2L)
    Assert.Equal("test-command", result.State.Persistent.Log.[2L].Command)

[<Fact>]
let ``NodeSnapshot.handleTakeSnapshot with committed entries trims log`` () =
    let mutable state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    let mutable leaderState = state
    leaderState <- Replication.appendCommand "cmd1" leaderState
    leaderState <- Replication.appendCommand "cmd2" leaderState
    leaderState <- State.updateLastApplied 3L leaderState
    State.updateCommitIndex 3L leaderState |> ignore
    let ctx = makeSnapshotCtx dummyConfig leaderState
    let result = NodeSnapshot.handleTakeSnapshot ctx "snap-data"
    Assert.True result.State.Persistent.Snapshot.IsSome
    Assert.Equal(3L, result.State.Persistent.Snapshot.Value.LastIncludedIndex)
    Assert.Equal("snap-data", result.State.Persistent.Snapshot.Value.StateMachineData)
    Assert.False(result.State.Persistent.Log.ContainsKey 1L)
    Assert.False(result.State.Persistent.Log.ContainsKey 2L)
    Assert.True(result.State.Persistent.Log.ContainsKey 3L)
    Assert.Equal(Log.NoOpCommand, result.State.Persistent.Log.[3L].Command)

[<Fact>]
let ``NodeSnapshot.autoSnapshotIfNeeded creates snapshot when log entry count reaches threshold`` () =
    let config =
        { dummyConfig with
            SnapshotAutoThreshold = 2 }

    let mutable state = State.init config None
    state <- State.initLeaderState state
    state <- Replication.appendCommand "x" state
    state <- Replication.appendCommand "y" state
    state <- State.updateLastApplied 3L state
    let ctx = makeSnapshotCtx config state
    let result = NodeSnapshot.autoSnapshotIfNeeded ctx state
    Assert.True result.Persistent.Snapshot.IsSome
    Assert.Equal("snap_data", result.Persistent.Snapshot.Value.StateMachineData)

[<Fact>]
let ``NodeSnapshot.autoSnapshotIfNeeded uses existing snapshot lastIndex when computing log count`` () =
    let config =
        { dummyConfig with
            SnapshotAutoThreshold = 2 }

    let baseState = State.init config None

    let log =
        logFromList
            [ { Index = 2L
                Term = 1L
                Command = ""
                ClientId = None
                SeqNum = None }
              { Index = 3L
                Term = 1L
                Command = "a"
                ClientId = None
                SeqNum = None }
              { Index = 4L
                Term = 1L
                Command = "b"
                ClientId = None
                SeqNum = None }
              { Index = 5L
                Term = 1L
                Command = "c"
                ClientId = None
                SeqNum = None } ]

    let state =
        { baseState with
            Persistent =
                { baseState.Persistent with
                    Log = log
                    Snapshot =
                        Some
                            { LastIncludedIndex = 2L
                              LastIncludedTerm = 1L
                              StateMachineData = "old_snap" } }
            Volatile =
                { baseState.Volatile with
                    LastApplied = 5L } }

    let ctx = makeSnapshotCtx config state
    let result = NodeSnapshot.autoSnapshotIfNeeded ctx state
    Assert.True result.Persistent.Snapshot.IsSome
    Assert.Equal("snap_data", result.Persistent.Snapshot.Value.StateMachineData)

[<Fact>]
let ``NodeSnapshot.autoSnapshotIfNeeded is no-op when SnapshotAutoThreshold is 0`` () =
    let state = State.init dummyConfig None
    let ctx = makeSnapshotCtx dummyConfig state
    let result = NodeSnapshot.autoSnapshotIfNeeded ctx state
    Assert.Same(state, result)

[<Fact>]
let ``NodeSnapshot.autoSnapshotIfNeeded is no-op when log entry count is below threshold`` () =
    let config =
        { dummyConfig with
            SnapshotAutoThreshold = 10 }

    let state = State.init config None
    let ctx = makeSnapshotCtx config state
    let result = NodeSnapshot.autoSnapshotIfNeeded ctx state
    Assert.Same(state, result)
