module Raft.Tests.NodeSnapshotTests

open Xunit
open Raft
open TestHelpers

let makeSnapshotCtx config state =
    let inbox = new MailboxProcessor<NodeMessage>(fun _ -> async { () })
    let cts = new System.Threading.CancellationTokenSource()

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
      CancellationTokenSource = cts
      PendingReads = [] }

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
