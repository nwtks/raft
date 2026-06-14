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
          Reply = ignore
          Responses = Set.ofList [ 1; 2; 3 ] }

    Assert.True(NodeRead.canServePendingRead state pendingRead)

[<Fact>]
let ``NodeRead.canServePendingRead returns false when node is not leader`` () =
    let state = State.init dummyConfig None

    let pendingRead: PendingRead =
        { ReadIndex = 1L
          Reply = ignore
          Responses = Set.ofList [ 1; 2; 3 ] }

    Assert.False(NodeRead.canServePendingRead state pendingRead)

[<Fact>]
let ``NodeRead.canServePendingRead returns false when commit index is 0`` () =
    let state = State.initLeaderState (State.init dummyConfig None)

    let pendingRead: PendingRead =
        { ReadIndex = 0L
          Reply = ignore
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
          Reply = ignore
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
          Reply = ignore
          Responses = Set.ofList [ 1 ] }

    Assert.False(NodeRead.canServePendingRead state pendingRead)

[<Fact>]
let ``NodeRead.classifyPendingReads resolves read on leader when quorum reached and commit matches`` () =
    let state =
        { State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None)) with
            Volatile = { CommitIndex = 1L; LastApplied = 1L } }

    let pendingRead: PendingRead =
        { ReadIndex = 1L
          Reply = ignore
          Responses = Set.ofList [ 1; 2; 3 ] }

    let remaining, resolved = NodeRead.classifyPendingReads [ pendingRead ] state
    Assert.Empty remaining
    Assert.Single resolved |> ignore
    Assert.Equal(ReadReady, snd resolved.[0])

[<Fact>]
let ``NodeRead.classifyPendingReads keeps pending read when quorum not reached`` () =
    let state =
        { State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None)) with
            Volatile = { CommitIndex = 1L; LastApplied = 1L } }

    let pendingRead: PendingRead =
        { ReadIndex = 1L
          Reply = ignore
          Responses = Set.ofList [ 1 ] }

    let remaining, resolved = NodeRead.classifyPendingReads [ pendingRead ] state
    Assert.Single remaining |> ignore
    Assert.Empty resolved

[<Fact>]
let ``NodeRead.classifyPendingReads redirects all reads on follower`` () =
    let state = State.init dummyConfig None

    let pendingRead: PendingRead =
        { ReadIndex = 1L
          Reply = ignore
          Responses = Set.ofList [ 1 ] }

    let remaining, resolved = NodeRead.classifyPendingReads [ pendingRead ] state
    Assert.Empty remaining
    Assert.Single resolved |> ignore

    match snd resolved.[0] with
    | ReadRedirect _ -> ()
    | ReadReady -> Assert.Fail "Expected ReadRedirect on follower"
