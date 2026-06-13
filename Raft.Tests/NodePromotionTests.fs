module Raft.Tests.NodePromotionTests

open Xunit
open Raft
open TestHelpers

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
let ``NodePromotion.tryFinalizeConfiguration is no-op when last entry is already FinalChange`` () =
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
let ``NodePromotion.getReadyPeers returns empty when LeaderState is None`` () =
    let state = State.init dummyConfig None
    let ready = NodePromotion.getReadyPeers state
    Assert.Empty ready

[<Fact>]
let ``NodePromotion.tryPromoteNonVotingPeers promotes caught-up non-voting peer`` () =
    let config = configWithPeers 1 0
    let state = State.initLeaderState (State.init config None)

    let ctx =
        { makeNodeContext state with
            Config = config }

    let newPeer = { Id = 4; Host = "127.0.0.1"; Port = 0 }

    let addedState =
        { state with
            NonVotingPeers = newPeer :: state.NonVotingPeers
            LeaderState =
                state.LeaderState
                |> Option.map (fun ls ->
                    { ls with
                        NextIndex = ls.NextIndex |> Map.add newPeer.Id (Log.lastIndex state.Persistent.Log + 1L)
                        MatchIndex = ls.MatchIndex |> Map.add newPeer.Id Log.initialLogIndex }) }

    Assert.Contains(newPeer, addedState.NonVotingPeers)
    Assert.Equal(1, addedState.Persistent.Log.Count)

    let stateWithMatch =
        { addedState with
            LeaderState =
                addedState.LeaderState
                |> Option.map (fun ls ->
                    { ls with
                        MatchIndex = ls.MatchIndex |> Map.add newPeer.Id (Log.lastIndex addedState.Persistent.Log) }) }

    let result = NodePromotion.tryPromoteNonVotingPeers ctx stateWithMatch
    Assert.False(result.NonVotingPeers |> List.exists (fun p -> p.Id = newPeer.Id))
    Assert.Equal(2, result.Persistent.Log.Count)
    Assert.StartsWith(ConfigChange.ConfigCommandPrefix, (Map.find 2L result.Persistent.Log).Command)

[<Fact>]
let ``NodePromotion.tryPromoteNonVotingPeers is no-op when no peer is caught up`` () =
    let config = configWithPeers 1 0
    let state = State.initLeaderState (State.init config None)

    let ctx =
        { makeNodeContext state with
            Config = config }

    let newPeer = { Id = 4; Host = "127.0.0.1"; Port = 0 }

    let stateWithPeer =
        { state with
            NonVotingPeers = newPeer :: state.NonVotingPeers
            LeaderState =
                state.LeaderState
                |> Option.map (fun ls ->
                    { ls with
                        NextIndex = ls.NextIndex |> Map.add newPeer.Id (Log.lastIndex state.Persistent.Log + 1L)
                        MatchIndex = ls.MatchIndex |> Map.add newPeer.Id Log.initialLogIndex }) }

    let result = NodePromotion.tryPromoteNonVotingPeers ctx stateWithPeer
    Assert.Contains(newPeer, result.NonVotingPeers)
    Assert.Same(stateWithPeer, result)
