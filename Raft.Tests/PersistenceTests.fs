module Raft.Tests.PersistenceTests

open Xunit
open Raft

let getTestNodeId () = System.Random().Next(10000, 99999)

[<Fact>]
let ``FilePersistence.Load returns None when state file does not exist`` () =
    let nodeId = getTestNodeId ()
    let persistence = FilePersistence nodeId :> IPersistence
    let fileName = sprintf "state_%d.json" nodeId

    if System.IO.File.Exists fileName then
        System.IO.File.Delete fileName

    let result = persistence.Load()
    Assert.True result.IsNone

[<Fact>]
let ``FilePersistence.Save serializes and Load deserializes state correctly`` () =
    let nodeId = getTestNodeId ()
    let persistence = FilePersistence nodeId :> IPersistence
    let fileName = sprintf "state_%d.json" nodeId

    let originalState: PersistentState =
        { CurrentTerm = 42L
          VotedFor = Some 3
          Log =
            [ { Index = 1L
                Term = 40L
                Command = "put x 1" }
              { Index = 2L
                Term = 42L
                Command = "put y 2" } ] }

    try
        persistence.Save originalState
        let loadedStateOpt = persistence.Load()
        Assert.True loadedStateOpt.IsSome

        let loadedState = loadedStateOpt.Value
        Assert.Equal(42L, loadedState.CurrentTerm)
        Assert.Equal(Some 3, loadedState.VotedFor)
        Assert.Equal(2, loadedState.Log.Length)
        Assert.Equal("put x 1", loadedState.Log.[0].Command)
        Assert.Equal("put y 2", loadedState.Log.[1].Command)
    finally
        if System.IO.File.Exists fileName then
            System.IO.File.Delete fileName

[<Fact>]
let ``FilePersistence.Save overwrites previously saved state with new state`` () =
    let nodeId = getTestNodeId ()
    let persistence = FilePersistence nodeId :> IPersistence
    let fileName = sprintf "state_%d.json" nodeId

    let state1: PersistentState =
        { CurrentTerm = 1L
          VotedFor = None
          Log = [] }

    let state2: PersistentState =
        { CurrentTerm = 2L
          VotedFor = Some 5
          Log =
            [ { Index = 1L
                Term = 2L
                Command = "set a b" } ] }

    try
        persistence.Save state1
        persistence.Save state2
        let loadedStateOpt = persistence.Load()
        let loadedState = loadedStateOpt.Value
        Assert.Equal(2L, loadedState.CurrentTerm)
        Assert.Equal(Some 5, loadedState.VotedFor)
    finally
        if System.IO.File.Exists fileName then
            System.IO.File.Delete fileName
