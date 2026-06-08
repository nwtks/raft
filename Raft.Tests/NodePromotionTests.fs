module Raft.Tests.NodePromotionTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``NodePromotion.tryFinalizeConfiguration is no-op when not in JointPhase`` () =
    let state = State.init dummyConfig None
    let result = NodePromotion.tryFinalizeConfiguration state
    Assert.Same(state, result)

[<Fact>]
let ``NodePromotion.tryFinalizeConfiguration is no-op when not leader`` () =
    let state = State.enterJointConsensus [] [] (State.init dummyConfig None)
    let result = NodePromotion.tryFinalizeConfiguration state
    Assert.Same(state, result)

[<Fact>]
let ``NodePromotion.tryFinalizeConfiguration does nothing when last entry is already FinalChange`` () =
    let mutable state = State.initLeaderState (State.init dummyConfig None)
    let c2 = dummyConfig.Peers |> List.filter (fun p -> p.Id = 2)
    let c3 = dummyConfig.Peers |> List.filter (fun p -> p.Id = 3)
    state <- Replication.appendJointConsensus c2 c3 state
    state <- Replication.appendFinalConfiguration c2 state
    let preState = state
    state <- State.enterJointConsensus c2 c3 state

    state <-
        { state with
            Role = Leader
            LeaderState = preState.LeaderState }

    let result = NodePromotion.tryFinalizeConfiguration state
    Assert.Same(state, result)

[<Fact>]
let ``NodePromotion.tryFinalizeConfiguration appends FinalConfiguration when last entry is JointChange`` () =
    let mutable state = State.initLeaderState (State.init dummyConfig None)
    let c2 = dummyConfig.Peers |> List.filter (fun p -> p.Id = 2)
    let c3 = dummyConfig.Peers |> List.filter (fun p -> p.Id = 3)
    state <- Replication.appendJointConsensus c2 c3 state
    let preState = state
    state <- State.enterJointConsensus c2 c3 state

    state <-
        { state with
            Role = Leader
            LeaderState = preState.LeaderState }

    let result = NodePromotion.tryFinalizeConfiguration state
    Assert.Equal(3, result.Persistent.Log.Count)
    let lastEntry = Log.getEntry 3L result.Persistent.Log
    Assert.True lastEntry.IsSome
    Assert.StartsWith(ConfigChange.ConfigCommandPrefix, lastEntry.Value.Command)

[<Fact>]
let ``NodePromotion.tryFinalizeConfiguration appends FinalConfiguration when last entry is a normal command`` () =
    let mutable state = State.initLeaderState (State.init dummyConfig None)
    state <- Replication.appendCommand "cmd1" state
    let preState = state
    let c2 = dummyConfig.Peers |> List.filter (fun p -> p.Id = 2)
    let c3 = dummyConfig.Peers |> List.filter (fun p -> p.Id = 3)
    state <- State.enterJointConsensus c2 c3 state

    state <-
        { state with
            Role = Leader
            LeaderState = preState.LeaderState }

    let result = NodePromotion.tryFinalizeConfiguration state
    Assert.Equal(3, result.Persistent.Log.Count)
    let lastEntry = Log.getEntry 3L result.Persistent.Log
    Assert.True lastEntry.IsSome
    Assert.StartsWith(ConfigChange.ConfigCommandPrefix, lastEntry.Value.Command)
