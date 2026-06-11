module Raft.Tests.NodeTimeoutTests

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
let ``NodeTimeout.handleElectionTimeout promotes follower to leader in single-node cluster`` () =
    let config = { dummyConfig with Peers = [] }
    let state = State.init config None

    let ctx =
        { makeTestContext state with
            Config = config }

    let result = NodeTimeout.handleElectionTimeout ctx
    Assert.Equal(Leader, result.State.Role)
    Assert.Equal(1L, result.State.Persistent.CurrentTerm)
    Assert.True result.State.LeaderState.IsSome

[<Fact>]
let ``NodeTimeout.handleElectionTimeout is no-op on leader`` () =
    let config = { dummyConfig with Peers = [] }
    let state = State.init config None

    let ctx =
        { makeTestContext state with
            Config = config }

    let firstResult = NodeTimeout.handleElectionTimeout ctx
    Assert.Equal(Leader, firstResult.State.Role)
    let firstTerm = firstResult.State.Persistent.CurrentTerm
    let ctx2 = { ctx with State = firstResult.State }
    let secondResult = NodeTimeout.handleElectionTimeout ctx2
    Assert.Equal(Leader, secondResult.State.Role)
    Assert.Equal(firstTerm, secondResult.State.Persistent.CurrentTerm)

[<Fact>]
let ``NodeTimeout.handleHeartbeatTimeout is no-op on follower`` () =
    let state = State.init dummyConfig None
    let ctx = makeTestContext state
    let result = NodeTimeout.handleHeartbeatTimeout ctx
    Assert.Equal(Follower, result.State.Role)
    Assert.Equal(state.Persistent.CurrentTerm, result.State.Persistent.CurrentTerm)
