module Raft.Tests.NodeLocalTests

open Xunit
open Raft
open TestHelpers

let makeTestContext state : NodeContext =
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
let ``NodeLocal.commitAndBroadcast persists state, calls apply, and returns final state`` () =
    let state = State.init dummyConfig None
    let persistence = MockPersistence() :> IPersistence

    let ctx =
        { makeTestContext state with
            Persistence = persistence }

    let leaderState = State.initLeaderState state

    let mutable appliedCount = 0
    let onApplied _ = appliedCount <- appliedCount + 1

    let result = NodeLocal.commitAndBroadcast ctx leaderState onApplied

    let loaded = persistence.Load()
    Assert.True loaded.IsSome
    Assert.Equal(leaderState.Persistent.CurrentTerm, loaded.Value.CurrentTerm)

    Assert.Equal(1, appliedCount)

    Assert.Equal(leaderState.Role, result.Role)
    Assert.Equal(leaderState.Persistent.Log.Count, result.Persistent.Log.Count)

[<Fact>]
let ``NodeLocal.dispatchLocalMessage returns unchanged state for unrecognized message`` () =
    let state = State.init dummyConfig None
    let ctx = makeTestContext state

    let result = NodeLocal.dispatchLocalMessage ctx ElectionTimeout

    Assert.Equal(state, result)
    Assert.Equal(Follower, result.Role)

[<Fact>]
let ``NodeLocal.dispatchLocalMessage returns unchanged state for ClientCommand on Follower`` () =
    let state = State.init dummyConfig None
    let ctx = makeTestContext state
    Assert.Equal(Follower, ctx.State.Role)

    let result = NodeLocal.dispatchLocalMessage ctx ElectionTimeout
    Assert.Equal(Follower, result.Role)

[<Fact>]
let ``NodeLocal.dispatchLocalMessage returns ctx.State for all wildcard messages`` () =
    let state = State.init dummyConfig None
    let ctx = makeTestContext state

    [ ElectionTimeout; HeartbeatTimeout ]
    |> List.iter (fun msg ->
        let result = NodeLocal.dispatchLocalMessage ctx msg
        Assert.Equal(state, result))
