module Raft.Tests.NodeRaftTests

open Xunit
open Raft
open TestHelpers

let makeTestContext state =
    let inbox = new MailboxProcessor<NodeMessage>(fun _ -> async { () })

    { Config = dummyConfig
      Transport = MockTransport() :> ITransport
      Persistence = MockPersistence() :> IPersistence
      OnApply = ignore
      OnInstallSnapshot = ignore
      OnGetSnapshotData = fun () -> ""
      Inbox = inbox
      State = state
      ElectionTimer = None
      HeartbeatTimer = None
      CancellationTokenSource = new System.Threading.CancellationTokenSource()
      PendingReads = [] }

[<Fact>]
let ``NodeAgent.postProcess uses result State.Config when it differs from ctx.Config`` () =
    let state = State.init dummyConfig None
    let ctx = makeTestContext state
    Assert.Equal(ctx.Config, state.Config)

    let modifiedState =
        { state with
            Config = { state.Config with Peers = [] } }

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
    let state = State.init dummyConfig None
    let ctx = makeTestContext state
    Assert.Equal(ctx.Config, state.Config)

    let result =
        { State = state
          ElectionAction = Keep
          HeartbeatAction = Keep
          PendingReads = [] }

    let newCtx = NodeAgent.postProcess ctx result

    Assert.Equal(ctx.Config, newCtx.Config)
    Assert.Equal(state, newCtx.State)
