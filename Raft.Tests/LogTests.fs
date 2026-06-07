module Raft.Tests.LogTests

open Xunit
open Raft
open TestHelpers

let createEntry index term cmd =
    { Index = index
      Term = term
      Command = cmd
      ClientId = None
      SeqNum = None }

[<Fact>]
let ``Log.empty returns lastIndex 0 and lastTerm 0`` () =
    let log = Log.empty
    Assert.Equal(0L, Log.lastIndex log)
    Assert.Equal(0L, Log.lastTerm log)

[<Fact>]
let ``Log.getEntry returns None for missing index`` () =
    let log = logFromList [ createEntry 1L 1L "a" ]
    Assert.True(Log.getEntry 99L log |> Option.isNone)

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
let ``Log.lastIndexOfTerm returns correct index when term is found`` () =
    let log =
        logFromList [ createEntry 1L 1L "a"; createEntry 2L 1L "b"; createEntry 3L 2L "c" ]

    let result = Log.lastIndexOfTerm 1L log
    Assert.Equal(Some 2L, result)

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
let ``Log.createEntry uses next sequential index`` () =
    let log = Log.empty |> Log.append 1L "a" |> Log.append 1L "b"
    let entry = Log.createEntry 2L "c" (Some "client-x") (Some 99L) log
    Assert.Equal(3L, entry.Index)
    Assert.Equal(2L, entry.Term)
    Assert.Equal("c", entry.Command)
    Assert.Equal(Some "client-x", entry.ClientId)
    Assert.Equal(Some 99L, entry.SeqNum)

[<Fact>]
let ``Log.append adds entry and returns incremented lastIndex`` () =
    let log = Log.empty |> Log.append 1L "cmd1"
    Assert.Equal(1L, Log.lastIndex log)
    Assert.Equal(1L, Log.lastTerm log)

    let entry = Log.getEntry 1L log
    Assert.True entry.IsSome
    Assert.Equal("cmd1", entry.Value.Command)

[<Fact>]
let ``Log.appendWithSession creates entry with clientId and seqNum`` () =
    let log = Log.empty |> Log.appendWithSession 1L "cmd-session" "client-1" 42L
    Assert.Equal(1L, Log.lastIndex log)

    let entry = Log.getEntry 1L log
    Assert.True entry.IsSome
    Assert.Equal("cmd-session", entry.Value.Command)
    Assert.Equal(Some "client-1", entry.Value.ClientId)
    Assert.Equal(Some 42L, entry.Value.SeqNum)

[<Fact>]
let ``Log.appendWithSession increments index correctly`` () =
    let log =
        Log.empty
        |> Log.append 1L "cmd1"
        |> Log.appendWithSession 2L "cmd2" "client-1" 1L
        |> Log.appendWithSession 2L "cmd3" "client-2" 5L

    Assert.Equal(3L, Log.lastIndex log)
    Assert.Equal("cmd1", (Log.getEntry 1L log).Value.Command)
    Assert.Equal(Some "client-1", (Log.getEntry 2L log).Value.ClientId)
    Assert.Equal(Some 5L, (Log.getEntry 3L log).Value.SeqNum)
    Assert.Equal(None, (Log.getEntry 1L log).Value.ClientId)
    Assert.Equal(None, (Log.getEntry 1L log).Value.SeqNum)

[<Fact>]
let ``Log.appendEntriesToLog adds multiple entries in batch`` () =
    let log = Log.empty |> Log.append 1L "a"

    let entries =
        [ { Index = 2L
            Term = 1L
            Command = "b"
            ClientId = None
            SeqNum = None }
          { Index = 3L
            Term = 1L
            Command = "c"
            ClientId = None
            SeqNum = None } ]

    let merged = Log.appendEntriesToLog log entries
    Assert.Equal(3, merged.Count)
    Assert.Equal("c", (Log.getEntry 3L merged).Value.Command)


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

[<Fact>]
let ``Log.mergeEntries with conflict at first entry truncates everything`` () =
    let log =
        logFromList
            [ createEntry 1L 1L "old_a"
              createEntry 2L 1L "old_b"
              createEntry 3L 2L "old_c" ]

    let newEntries = [ createEntry 1L 3L "new_a"; createEntry 2L 3L "new_b" ]
    let merged = Log.mergeEntries newEntries log

    Assert.Equal(2, merged.Count)
    Assert.Equal(3L, merged.[1L].Term)
    Assert.Equal("new_a", merged.[1L].Command)
    Assert.Equal("new_b", merged.[2L].Command)

[<Fact>]
let ``Log.trim removes entries at or below lastIncludedIndex and adds sentinel`` () =
    let log =
        logFromList
            [ createEntry 1L 1L "a"
              createEntry 2L 1L "b"
              createEntry 3L 2L "c"
              createEntry 4L 2L "d" ]

    let trimmed = Log.trim 2L 1L log

    Assert.Equal(3, trimmed.Count)

    Assert.True(trimmed.ContainsKey 2L)
    Assert.Equal("", trimmed.[2L].Command)
    Assert.Equal(1L, trimmed.[2L].Term)

    Assert.True(trimmed.ContainsKey 3L)
    Assert.Equal("c", trimmed.[3L].Command)
    Assert.True(trimmed.ContainsKey 4L)
    Assert.Equal("d", trimmed.[4L].Command)

    Assert.False(trimmed.ContainsKey 1L)

[<Fact>]
let ``Log.trim with empty log adds only sentinel`` () =
    let trimmed = Log.trim 1L 1L Map.empty
    Assert.Equal(1, trimmed.Count)
    Assert.Equal(1L, trimmed.[1L].Index)
    Assert.Equal("", trimmed.[1L].Command)
