module Raft.Tests.StateTests

open Xunit
open Raft

let dummyConfig =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = 15001
      Peers = []
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500 }

[<Fact>]
let ``State.init with None creates default PersistentState`` () =
    let state = State.init dummyConfig None

    Assert.Equal(Follower, state.Role)
    Assert.Equal(0L, state.Persistent.CurrentTerm)
    Assert.Equal(None, state.Persistent.VotedFor)
    Assert.Empty state.Persistent.Log
    Assert.Equal(0L, state.Volatile.CommitIndex)
    Assert.Equal(0L, state.Volatile.LastApplied)

[<Fact>]
let ``State.init with Some loadedState restores PersistentState correctly`` () =
    let loaded: PersistentState =
        { CurrentTerm = 5L
          VotedFor = Some 2
          Log = [ { Index = 1L; Term = 4L; Command = "x" } ] }

    let state = State.init dummyConfig (Some loaded)

    Assert.Equal(5L, state.Persistent.CurrentTerm)
    Assert.Equal(Some 2, state.Persistent.VotedFor)
    Assert.Single state.Persistent.Log |> ignore
    Assert.Equal(1L, state.Persistent.Log.[0].Index)
    Assert.Equal(Follower, state.Role)
    Assert.Equal(0L, state.Volatile.CommitIndex)
    Assert.Equal(0L, state.Volatile.LastApplied)
    Assert.True state.LeaderState.IsNone
