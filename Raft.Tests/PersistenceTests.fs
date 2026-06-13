module Raft.Tests.PersistenceTests

open Xunit
open Raft
open TestHelpers

let getTestNodeId () = System.Random().Next(10000, 99999)

[<Fact>]
let ``FilePersistence.Save and Load round-trip state`` () =
    let nodeId = getTestNodeId ()
    let persistence = FilePersistence nodeId :> IPersistence
    let fileName = sprintf "state_%d.json" nodeId

    let originalState: PersistentState =
        { CurrentTerm = 42L
          VotedFor = Some 3
          Log =
            logFromList
                [ { Index = 1L
                    Term = 40L
                    Command = "put x 1"
                    ClientId = None
                    SeqNum = None }
                  { Index = 2L
                    Term = 42L
                    Command = "put y 2"
                    ClientId = None
                    SeqNum = None } ]
          Snapshot = None
          SessionTable = Map.empty
          LastConfigIndex = 0L }

    try
        persistence.Save originalState
        let loadedStateOpt = persistence.Load()
        Assert.True loadedStateOpt.IsSome

        let loadedState = loadedStateOpt.Value
        Assert.Equal(42L, loadedState.CurrentTerm)
        Assert.Equal(Some 3, loadedState.VotedFor)
        Assert.Equal(2, loadedState.Log.Count)
        Assert.Equal("put x 1", (Map.find 1L loadedState.Log).Command)
        Assert.Equal("put y 2", (Map.find 2L loadedState.Log).Command)
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
          Log = Map.empty
          Snapshot = None
          SessionTable = Map.empty
          LastConfigIndex = 0L }

    let state2: PersistentState =
        { CurrentTerm = 2L
          VotedFor = Some 5
          Log =
            logFromList
                [ { Index = 1L
                    Term = 2L
                    Command = "set a b"
                    ClientId = None
                    SeqNum = None } ]
          Snapshot = None
          SessionTable = Map.empty
          LastConfigIndex = 0L }

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
let ``FilePersistence.Load returns None for corrupted JSON file`` () =
    let nodeId = getTestNodeId ()
    let persistence = FilePersistence nodeId :> IPersistence
    let fileName = sprintf "state_%d.json" nodeId

    try
        System.IO.File.WriteAllText(fileName, "this is not valid JSON")
        let result = persistence.Load()
        Assert.True result.IsNone
    finally
        if System.IO.File.Exists fileName then
            System.IO.File.Delete fileName

[<Fact>]
let ``FilePersistence.Load deletes orphaned .tmp file on startup`` () =
    let nodeId = getTestNodeId ()
    let persistence = FilePersistence nodeId :> IPersistence
    let fileName = sprintf "state_%d.json" nodeId
    let tempFileName = fileName + ".tmp"

    try
        System.IO.File.WriteAllText(tempFileName, "orphaned temp content")
        Assert.True(System.IO.File.Exists tempFileName)

        let result = persistence.Load()
        Assert.False(System.IO.File.Exists tempFileName, ".tmp file should be deleted during Load")
        Assert.True result.IsNone
    finally
        for f in [ fileName; tempFileName ] do
            if System.IO.File.Exists f then
                System.IO.File.Delete f
