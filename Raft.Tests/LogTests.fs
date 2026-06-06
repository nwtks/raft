module Raft.Tests.LogTests

open Xunit
open Raft
open TestHelpers

let createEntry index term cmd =
    { Index = index
      Term = term
      Command = cmd }

[<Fact>]
let ``Log.empty returns lastIndex 0 and lastTerm 0`` () =
    let log = Log.empty
    Assert.Equal(0L, Log.lastIndex log)
    Assert.Equal(0L, Log.lastTerm log)

[<Fact>]
let ``Log.termAt returns 0 for missing index`` () =
    let log = logFromList [ createEntry 1L 1L "a" ]
    Assert.Equal(0L, Log.termAt 2L log)

[<Fact>]
let ``Log.lastIndexOfTerm returns None when term not found`` () =
    let log = logFromList [ createEntry 1L 1L "a"; createEntry 2L 1L "b" ]
    let result = Log.lastIndexOfTerm 2L log
    Assert.True result.IsNone

[<Fact>]
let ``Log.append adds entry and returns incremented lastIndex`` () =
    let log = Log.empty |> Log.append 1L "cmd1"
    Assert.Equal(1L, Log.lastIndex log)
    Assert.Equal(1L, Log.lastTerm log)

    let entry = Log.getEntry 1L log
    Assert.True entry.IsSome
    Assert.Equal("cmd1", entry.Value.Command)

[<Fact>]
let ``Log.entriesFrom returns entries at and after given index`` () =
    let log =
        logFromList [ createEntry 1L 1L "a"; createEntry 2L 1L "b"; createEntry 3L 1L "c" ]

    let result = Log.entriesFrom 2L log
    Assert.Equal(2, result.Length)
    Assert.Equal(2L, result.[0].Index)
    Assert.Equal(3L, result.[1].Index)

[<Fact>]
let ``Log.entriesFrom beyond lastIndex returns empty list`` () =
    let log = logFromList [ createEntry 1L 1L "a" ]
    let result = Log.entriesFrom 5L log
    Assert.Empty result

[<Fact>]
let ``Log.mergeEntries appends new entries when no conflict exists`` () =
    let log = logFromList [ createEntry 1L 1L "cmd1" ]
    let newEntries = [ createEntry 2L 1L "cmd2" ]

    let merged = Log.mergeEntries newEntries log
    Assert.Equal(2, merged.Count)
    Assert.Equal(2L, Log.lastIndex merged)

[<Fact>]
let ``Log.mergeEntries truncates conflicting entries and replaces with leader entries`` () =
    let log =
        logFromList [ createEntry 1L 1L "cmd1"; createEntry 2L 1L "cmd2"; createEntry 3L 1L "cmd3" ]

    let newEntries = [ createEntry 2L 2L "new_cmd2"; createEntry 3L 2L "new_cmd3" ]

    let merged = Log.mergeEntries newEntries log
    Assert.Equal(3, merged.Count)
    Assert.Equal(2L, (Log.getEntry 2L merged).Value.Term)
    Assert.Equal("new_cmd2", (Log.getEntry 2L merged).Value.Command)

[<Fact>]
let ``Log.mergeEntries with empty entries returns original log unchanged`` () =
    let log = logFromList [ createEntry 1L 1L "cmd1" ]
    let merged = Log.mergeEntries [] log
    Assert.Equal(1, merged.Count)
    Assert.Equal(1L, Log.lastIndex merged)

[<Fact>]
let ``Log.mergeEntries into empty log adds all entries`` () =
    let newEntries = [ createEntry 1L 1L "a"; createEntry 2L 1L "b" ]
    let merged = Log.mergeEntries newEntries Map.empty
    Assert.Equal(2, merged.Count)
    Assert.Equal(2L, Log.lastIndex merged)

[<Fact>]
let ``Log.mergeEntries with exact matching entries does not duplicate`` () =
    let log = logFromList [ createEntry 1L 1L "a"; createEntry 2L 1L "b" ]
    let newEntries = [ createEntry 1L 1L "a"; createEntry 2L 1L "b" ]
    let merged = Log.mergeEntries newEntries log
    Assert.Equal(2, merged.Count)
