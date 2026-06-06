module Raft.Tests.StateTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``State.init without persisted state creates default PersistentState with term 0`` () =
    let state = State.init dummyConfigStandalone None

    Assert.Equal(Follower, state.Role)
    Assert.Equal(0L, state.Persistent.CurrentTerm)
    Assert.Equal(None, state.Persistent.VotedFor)
    Assert.Empty state.Persistent.Log
    Assert.Equal(0L, state.Volatile.CommitIndex)
    Assert.Equal(0L, state.Volatile.LastApplied)

[<Fact>]
let ``State.init with persisted state restores CurrentTerm, VotedFor, and Log correctly`` () =
    let loaded: PersistentState =
        { CurrentTerm = 5L
          VotedFor = Some 2
          Log = logFromList [ { Index = 1L; Term = 4L; Command = "x" } ]
          Snapshot = None }

    let state = State.init dummyConfigStandalone (Some loaded)

    Assert.Equal(5L, state.Persistent.CurrentTerm)
    Assert.Equal(Some 2, state.Persistent.VotedFor)
    Assert.Equal(1, state.Persistent.Log.Count)
    Assert.Equal(1L, (Map.find 1L state.Persistent.Log).Index)
    Assert.Equal(Follower, state.Role)
    Assert.Equal(0L, state.Volatile.CommitIndex)
    Assert.Equal(0L, state.Volatile.LastApplied)
    Assert.True state.LeaderState.IsNone
