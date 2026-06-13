module Raft.Tests.NodeLocalTests

open Xunit
open Raft
open TestHelpers

let makeLeaderWithPeerConfig () =
    let state =
        State.initLeaderState (State.updateTerm 1L (State.init dummyConfig None))

    state

[<Fact>]
let ``NodeLocal.commitAndBroadcast persists state, calls apply for committed entries, and returns final state`` () =
    let initial = State.init dummyConfig None
    let persistence = MockPersistence() :> IPersistence
    let mutable appliedCount = 0

    let ctx =
        { makeNodeContext initial with
            Persistence = persistence
            OnApply = fun _ -> appliedCount <- appliedCount + 1 }

    let logWithEntry = initial.Persistent.Log |> Log.append 1L "test_cmd"

    let stateWithEntry =
        { initial with
            Persistent =
                { initial.Persistent with
                    Log = logWithEntry }
            Volatile =
                { initial.Volatile with
                    CommitIndex = 1L } }

    let result = NodeLocal.commitAndBroadcast ctx stateWithEntry

    let loaded = persistence.Load()
    Assert.True loaded.IsSome
    Assert.Equal(stateWithEntry.Persistent.CurrentTerm, loaded.Value.CurrentTerm)
    Assert.Equal(1, appliedCount)
    Assert.Equal(stateWithEntry.Role, result.Role)
    Assert.Equal(stateWithEntry.Persistent.Log.Count, result.Persistent.Log.Count)

[<Fact>]
let ``NodeLocal.handleClientCommand returns Redirect when node is not leader`` () =
    let ctx = makeDefaultNodeContext ()
    let _, result = NodeLocal.handleClientCommand ctx "put a 1" None None

    match result with
    | Redirect _ -> ()
    | Accepted -> Assert.Fail "Expected redirect"

[<Fact>]
let ``NodeLocal.handleClientCommand returns Redirect with known leader when not leader`` () =
    let state =
        { State.init dummyConfig None with
            CurrentLeader = Some 2 }

    let ctx = makeNodeContext state
    let _, result = NodeLocal.handleClientCommand ctx "put a 1" None None

    match result with
    | Redirect(Some peer) -> Assert.Equal(2, peer.Id)
    | _ -> Assert.Fail $"Expected Redirect (Some peer) but got {result}"

[<Fact>]
let ``NodeLocal.handleAddPeer adds as non-voting member and broadcasts`` () =
    let state = makeLeaderWithPeerConfig ()
    let ctx, transport = makeNodeContextWithTransport state
    let newPeer = { Id = 4; Host = "127.0.0.1"; Port = 0 }
    let resultState, success = NodeLocal.handleAddPeer ctx newPeer
    Assert.True success
    Assert.Contains(newPeer, resultState.NonVotingPeers)
    Assert.Contains(transport.Messages, fun (p, _) -> p.Id = 4)

[<Fact>]
let ``NodeLocal.handleAddPeer returns true for already existing voting peer`` () =
    let state = makeLeaderWithPeerConfig ()
    let ctx = makeNodeContext state
    let existingPeer = { Id = 2; Host = "127.0.0.1"; Port = 0 }
    let _, success = NodeLocal.handleAddPeer ctx existingPeer
    Assert.True success

[<Fact>]
let ``NodeLocal.handleAddPeer returns false when not leader`` () =
    let ctx = makeDefaultNodeContext ()

    let _, success =
        NodeLocal.handleAddPeer ctx { Id = 2; Host = "127.0.0.1"; Port = 0 }

    Assert.False success

[<Fact>]
let ``NodeLocal.handleAddPeer handles leader without LeaderState gracefully`` () =
    let state =
        { State.updateTerm 1L (State.init dummyConfig None) with
            Role = Leader
            LeaderState = None }

    let ctx, transport = makeNodeContextWithTransport state
    let newPeer = { Id = 4; Host = "127.0.0.1"; Port = 0 }
    let resultState, success = NodeLocal.handleAddPeer ctx newPeer
    Assert.True success
    Assert.Contains(newPeer, resultState.NonVotingPeers)
    Assert.Empty transport.Messages

[<Fact>]
let ``NodeLocal.handleRemovePeer appends configuration entry and broadcasts`` () =
    let state = makeLeaderWithPeerConfig ()
    let ctx, transport = makeNodeContextWithTransport state

    let resultState, success = NodeLocal.handleRemovePeer ctx 3
    Assert.True success
    Assert.Equal(2, resultState.Persistent.Log.Count)
    Assert.StartsWith(ConfigChange.ConfigCommandPrefix, (Map.find 2L resultState.Persistent.Log).Command)
    Assert.Contains(transport.Messages, fun (p, _) -> p.Id = 2)

[<Fact>]
let ``NodeLocal.handleRemovePeer returns false when not leader`` () =
    let ctx = makeDefaultNodeContext ()
    let _, success = NodeLocal.handleRemovePeer ctx 2
    Assert.False success

[<Fact>]
let ``NodeLocal.dispatchLocalMessage returns unchanged state for unrecognized message`` () =
    let ctx = makeDefaultNodeContext ()
    let result = NodeLocal.dispatchLocalMessage ctx ElectionTimeout
    Assert.Equal(ctx.State, result)
    Assert.Equal(Follower, result.Role)

[<Fact>]
let ``NodeLocal.dispatchLocalMessage returns unchanged state for ClientCommand on Follower`` () =
    let ctx = makeDefaultNodeContext ()
    Assert.Equal(Follower, ctx.State.Role)

    let result = NodeLocal.dispatchLocalMessage ctx ElectionTimeout
    Assert.Equal(Follower, result.Role)

[<Fact>]
let ``NodeLocal.dispatchLocalMessage returns ctx.State for all wildcard messages`` () =
    let ctx = makeDefaultNodeContext ()

    [ ElectionTimeout; HeartbeatTimeout ]
    |> List.iter (fun msg ->
        let result = NodeLocal.dispatchLocalMessage ctx msg
        Assert.Equal(ctx.State, result))
