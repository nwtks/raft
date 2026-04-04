module Raft.Tests.PersistenceTests

open System
open System.IO
open Xunit
open Raft

let getTestNodeId () = Random().Next(10000, 99999)

[<Fact>]
let ``Load returns None when file does not exist`` () =
    let nodeId = getTestNodeId ()
    let persistence = FilePersistence nodeId :> IPersistence
    let fileName = sprintf "state_%d.json" nodeId

    if File.Exists fileName then
        File.Delete fileName

    let result = persistence.Load()
    Assert.True result.IsNone

[<Fact>]
let ``Save correctly serializes and Load correctly deserializes the state`` () =
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
        if File.Exists fileName then
            File.Delete fileName

[<Fact>]
let ``Save overwrites previous state successfully`` () =
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
        if File.Exists fileName then
            File.Delete fileName
