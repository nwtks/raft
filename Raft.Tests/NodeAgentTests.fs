module Raft.Tests.NodeAgentTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``NodeAgent.postProcess uses update State.Config when it differs from ctx.Config`` () =
    let ctx = makeDefaultNodeContext ()

    let modifiedState =
        { ctx.State with
            Config = { ctx.State.Config with Peers = [] } }

    Assert.NotEqual(ctx.Config, modifiedState.Config)

    let result =
        { State = modifiedState
          ElectionAction = Keep
          HeartbeatAction = Keep
          PendingReads = [] }

    let newCtx = NodeAgent.postProcess ctx result
    Assert.Equal(modifiedState.Config, newCtx.Config)
    Assert.Equal(modifiedState, newCtx.State)

[<Fact>]
let ``NodeAgent.postProcess keeps ctx.Config when result State.Config matches ctx.Config`` () =
    let ctx = makeDefaultNodeContext ()
    Assert.Equal(ctx.Config, ctx.State.Config)

    let result =
        { State = ctx.State
          ElectionAction = Keep
          HeartbeatAction = Keep
          PendingReads = [] }

    let newCtx = NodeAgent.postProcess ctx result
    Assert.Equal(ctx.Config, newCtx.Config)
    Assert.Equal(ctx.State, newCtx.State)

[<Fact>]
let ``NodeAgent.handleGetState replies with current state`` () =
    let ctx = makeDefaultNodeContext ()
    let mutable captured: RaftState option = None
    let msgResult = NodeAgent.handleGetState ctx (fun s -> captured <- Some s)
    Assert.Equal(Some ctx.State, captured)
    Assert.Equal(ctx.State, msgResult.State)

[<Fact>]
let ``NodeAgent.handleLinearizableRead returns ReadRedirect on non-leader`` () =
    let ctx = makeDefaultNodeContext ()
    let mutable result = Unchecked.defaultof<ReadCommandResult>
    let msgResult = NodeAgent.handleLinearizableRead ctx (fun r -> result <- r)

    match result with
    | ReadRedirect _ -> ()
    | ReadReady -> Assert.Fail "Expected ReadRedirect"

    Assert.Equal(Keep, msgResult.ElectionAction)
    Assert.Equal(Keep, msgResult.HeartbeatAction)
    Assert.Empty msgResult.PendingReads

[<Fact>]
let ``NodeAgent.handleLinearizableRead creates pending read on leader`` () =
    let state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    let ctx = makeNodeContext state
    let mutable result = Unchecked.defaultof<ReadCommandResult>
    let msgResult = NodeAgent.handleLinearizableRead ctx (fun r -> result <- r)
    Assert.Equal(1, msgResult.PendingReads.Length)

    let pr = msgResult.PendingReads.[0]
    Assert.Equal(0L, pr.ReadIndex)
    Assert.Equal(Keep, msgResult.ElectionAction)
    Assert.Equal(Keep, msgResult.HeartbeatAction)

[<Fact>]
let ``NodeAgent.handleTakeSnapshot calls NodeSnapshot.handleTakeSnapshot and replies`` () =
    let mutable state = State.init dummyConfig None
    state <- State.initLeaderState state
    state <- State.updateLastApplied 1L state

    let ctx =
        { makeNodeContext state with
            OnGetSnapshotData = fun () -> "snap_data" }

    let mutable replied = false

    let msgResult =
        NodeAgent.handleTakeSnapshot ctx "test-snap" (fun () -> replied <- true)

    Assert.True replied
    Assert.True msgResult.State.Persistent.Snapshot.IsSome
    Assert.Equal("test-snap", msgResult.State.Persistent.Snapshot.Value.StateMachineData)

[<Fact>]
let ``NodeAgent.handleMessage dispatches TakeSnapshot to handleTakeSnapshot`` () =
    let mutable state = State.init dummyConfig None
    state <- State.initLeaderState state
    state <- State.updateLastApplied 1L state

    let ctx =
        { makeNodeContext state with
            OnGetSnapshotData = fun () -> "snap_data" }

    let mutable replied = false

    let _ =
        NodeAgent.handleMessage ctx (TakeSnapshot("snapshot-data", fun () -> replied <- true))

    Assert.True replied

[<Fact>]
let ``NodeAgent.handleShutdown disposes timers and cancels token`` () =
    let ctx = makeDefaultNodeContext ()
    let mutable replied = false
    NodeAgent.handleShutdown ctx (fun () -> replied <- true)
    Assert.True replied
    Assert.True ctx.CancellationTokenSource.IsCancellationRequested
