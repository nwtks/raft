module Raft.Tests.NodeAgentTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``NodeAgent.postProcess uses result State.Config when it differs from ctx.Config`` () =
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
