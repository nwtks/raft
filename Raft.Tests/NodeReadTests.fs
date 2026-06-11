module Raft.Tests.NodeReadTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``NodeRead.canServePendingRead returns true when leader has committed entry in current term with quorum`` () =
    let mutable state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    state <-
        { state with
            Volatile = { state.Volatile with CommitIndex = 1L } }

    let pendingRead: PendingRead =
        { ReadIndex = 1L
          ReplyChannel = Unchecked.defaultof<_>
          Responses = Set.ofList [ 1; 2; 3 ] }

    Assert.True(NodeRead.canServePendingRead state pendingRead)

[<Fact>]
let ``NodeRead.canServePendingRead returns false when node is not leader`` () =
    let state = State.init dummyConfig None

    let pendingRead: PendingRead =
        { ReadIndex = 1L
          ReplyChannel = Unchecked.defaultof<_>
          Responses = Set.ofList [ 1; 2; 3 ] }

    Assert.False(NodeRead.canServePendingRead state pendingRead)

[<Fact>]
let ``NodeRead.canServePendingRead returns false when commit index is 0`` () =
    let state = State.initLeaderState (State.init dummyConfig None)

    let pendingRead: PendingRead =
        { ReadIndex = 0L
          ReplyChannel = Unchecked.defaultof<_>
          Responses = Set.ofList [ 1; 2; 3 ] }

    Assert.False(NodeRead.canServePendingRead state pendingRead)

[<Fact>]
let ``NodeRead.canServePendingRead returns false when commit index term != current term`` () =
    let mutable state = State.initLeaderState (State.init dummyConfig None)

    state <-
        { state with
            Volatile = { state.Volatile with CommitIndex = 1L }
            Persistent =
                { state.Persistent with
                    Log = Log.append state.Persistent.CurrentTerm "x" state.Persistent.Log
                    CurrentTerm = 2L } }

    let pendingRead: PendingRead =
        { ReadIndex = 1L
          ReplyChannel = Unchecked.defaultof<_>
          Responses = Set.ofList [ 1; 2; 3 ] }

    Assert.False(NodeRead.canServePendingRead state pendingRead)

[<Fact>]
let ``NodeRead.canServePendingRead returns false when peer quorum is not reached`` () =
    let mutable state = State.initLeaderState (State.init dummyConfig None)

    state <-
        { state with
            Volatile = { state.Volatile with CommitIndex = 1L } }

    let pendingRead: PendingRead =
        { ReadIndex = 1L
          ReplyChannel = Unchecked.defaultof<_>
          Responses = Set.ofList [ 1 ] }

    Assert.False(NodeRead.canServePendingRead state pendingRead)
