module Raft.Tests.NodeRaftTests

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
let ``NodeRaft.handleRaftRPCResult updates Config when state.Config differs from ctx.Config`` () =
    let state = State.init dummyConfig None
    let ctx = makeTestContext state

    Assert.Equal(ctx.Config, state.Config)

    let modifiedState =
        { state with
            Config = { state.Config with Peers = [] } }

    Assert.NotEqual(ctx.Config, modifiedState.Config)

    let rpcMsg =
        RequestVoteMsg
            { CandidateTerm = 1L
              CandidateId = 2
              LastLogIndex = 0L
              LastLogTerm = 0L }

    let result = NodeRaft.handleRaftRPCResult ctx rpcMsg modifiedState None None

    Assert.Equal(modifiedState.Config, result.Config)
    Assert.Equal(modifiedState, result.State)

[<Fact>]
let ``NodeRaft.handleRaftRPCResult keeps original Config when state.Config equals ctx.Config`` () =
    let state = State.init dummyConfig None
    let ctx = makeTestContext state

    Assert.Equal(ctx.Config, state.Config)

    let rpcMsg =
        RequestVoteMsg
            { CandidateTerm = 1L
              CandidateId = 2
              LastLogIndex = 0L
              LastLogTerm = 0L }

    let result = NodeRaft.handleRaftRPCResult ctx rpcMsg state None None

    Assert.Equal(ctx.Config, result.Config)
    Assert.Equal(state, result.State)
