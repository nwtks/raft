module Raft.Tests.NodeTimerTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``NodeTimer.getRandomElectionTimeout returns value within configured range`` () =
    let interval = NodeTimer.getRandomElectionTimeout dummyConfig
    Assert.InRange(interval, dummyConfig.ElectionTimeoutMinMs, dummyConfig.ElectionTimeoutMaxMs - 1)

[<Fact>]
let ``NodeTimer.createTimer returns a non-null timer`` () =
    let inbox = new MailboxProcessor<NodeMessage>(fun _ -> async { () })
    let timer = NodeTimer.createTimer inbox ElectionTimeout 10000
    Assert.NotNull timer

[<Fact>]
let ``NodeTimer.disposeTimer does nothing for None`` () = NodeTimer.disposeTimer None

[<Fact>]
let ``NodeTimer.disposeTimer disposes Some timer`` () =
    let inbox = new MailboxProcessor<NodeMessage>(fun _ -> async { () })
    let timer = NodeTimer.createTimer inbox ElectionTimeout 10000
    NodeTimer.disposeTimer (Some timer)

[<Fact>]
let ``NodeTimer.applyElectionAction returns ctx.ElectionTimer for Keep`` () =
    let ctx = makeDefaultNodeContext ()
    let result = NodeTimer.applyElectionAction ctx Keep
    Assert.Equal(ctx.ElectionTimer, result)

[<Fact>]
let ``NodeTimer.applyElectionAction Reset updates existing timer when present`` () =
    let ctx = makeDefaultNodeContext ()
    let timer = NodeTimer.createTimer ctx.Inbox ElectionTimeout 10000
    let ctxWithTimer = { ctx with ElectionTimer = Some timer }
    let result = NodeTimer.applyElectionAction ctxWithTimer Reset
    Assert.True result.IsSome

[<Fact>]
let ``NodeTimer.applyElectionAction Reset creates new timer when None`` () =
    let ctx = makeDefaultNodeContext ()
    Assert.Null ctx.ElectionTimer

    let result = NodeTimer.applyElectionAction ctx Reset
    Assert.True result.IsSome

[<Fact>]
let ``NodeTimer.applyElectionAction Stop disables existing timer when present`` () =
    let ctx = makeDefaultNodeContext ()
    let timer = NodeTimer.createTimer ctx.Inbox ElectionTimeout 10000
    let ctxWithTimer = { ctx with ElectionTimer = Some timer }
    let result = NodeTimer.applyElectionAction ctxWithTimer Stop
    Assert.True result.IsSome

[<Fact>]
let ``NodeTimer.applyElectionAction Stop does nothing when timer is None`` () =
    let ctx = makeDefaultNodeContext ()
    Assert.Null ctx.ElectionTimer

    let result = NodeTimer.applyElectionAction ctx Stop
    Assert.Null result

[<Fact>]
let ``NodeTimer.applyHeartbeatAction returns ctx.HeartbeatTimer for Keep`` () =
    let ctx = makeDefaultNodeContext ()
    let result = NodeTimer.applyHeartbeatAction ctx Keep
    Assert.Equal(ctx.HeartbeatTimer, result)

[<Fact>]
let ``NodeTimer.applyHeartbeatAction Reset updates existing timer when present`` () =
    let ctx = makeDefaultNodeContext ()
    let timer = NodeTimer.createTimer ctx.Inbox HeartbeatTimeout 10000
    let ctxWithTimer = { ctx with HeartbeatTimer = Some timer }
    let result = NodeTimer.applyHeartbeatAction ctxWithTimer Reset
    Assert.True result.IsSome

[<Fact>]
let ``NodeTimer.applyHeartbeatAction Reset creates new timer when None`` () =
    let ctx = makeDefaultNodeContext ()
    Assert.Null ctx.HeartbeatTimer

    let result = NodeTimer.applyHeartbeatAction ctx Reset
    Assert.True result.IsSome

[<Fact>]
let ``NodeTimer.applyHeartbeatAction Stop disables existing timer when present`` () =
    let ctx = makeDefaultNodeContext ()
    let timer = NodeTimer.createTimer ctx.Inbox HeartbeatTimeout 10000
    let ctxWithTimer = { ctx with HeartbeatTimer = Some timer }
    let result = NodeTimer.applyHeartbeatAction ctxWithTimer Stop
    Assert.True result.IsSome

[<Fact>]
let ``NodeTimer.applyHeartbeatAction Stop does nothing when timer is None`` () =
    let ctx = makeDefaultNodeContext ()
    Assert.Null ctx.HeartbeatTimer

    let result = NodeTimer.applyHeartbeatAction ctx Stop
    Assert.Null result

[<Fact>]
let ``NodeTimer.getTimerActionsOnRoleChange returns Keep Keep when role unchanged`` () =
    let ctx = makeDefaultNodeContext ()
    let state = ctx.State

    let election, heartbeat =
        NodeTimer.getTimerActionsOnRoleChange ctx Follower state false

    Assert.Equal(Keep, election)
    Assert.Equal(Keep, heartbeat)
