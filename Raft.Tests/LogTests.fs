module Raft.Tests.LogTests

open Xunit
open Raft
open TestHelpers

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
let ``Log.termAt returns 0 for non-existent index`` () =
    let log = logFromList [ createEntry 1L 1L "a" ]
    Assert.Equal(0L, Log.termAt 2L log)

[<Theory>]
[<InlineData("1:1,2:1", 2L, -1L)>] // term 2 not found → None
[<InlineData("1:1,2:1,3:2", 1L, 2L)>] // term 1 found at index 2
let ``Log.lastIndexOfTerm returns last index for given term`` (logSpec: string, searchTerm: int64, expected: int64) =
    let log =
        logSpec.Split(',')
        |> Array.map (fun s ->
            let parts = s.Split(':')
            createEntry (int64 parts.[0]) (int64 parts.[1]) "x")
        |> Array.toList
        |> logFromList

    let result = Log.lastIndexOfTerm searchTerm log

    if expected = -1L then
        Assert.True result.IsNone
    else
        Assert.Equal(Some expected, result)

[<Fact>]
let ``Log.entriesFrom returns entries from given index onward`` () =
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
let ``Log.append adds entry and updates lastIndex`` () =
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
let ``Log.appendWithSession increments lastIndex`` () =
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


[<Theory>]
[<InlineData("1:1:cmd1", "2:1:cmd2", 2, 2L, 1L)>] // append new entries
[<InlineData("1:1:cmd1,2:1:cmd2,3:1:cmd3", "2:2:new_cmd2,3:2:new_cmd3", 3, 3L, 2L)>] // truncate and replace
[<InlineData("1:1:cmd1", "", 1, 1L, 1L)>] // empty new entries → unchanged
[<InlineData("", "1:1:a,2:1:b", 2, 2L, 1L)>] // empty log → add all
[<InlineData("1:1:a,2:1:b", "1:1:a,2:1:b", 2, 2L, 1L)>] // exact match → no duplicate
[<InlineData("1:1:old_a,2:1:old_b,3:2:old_c", "1:3:new_a,2:3:new_b", 2, 2L, 3L)>] // conflict at first entry
let ``Log.mergeEntries merges incoming entries over existing log``
    (logSpec: string, newEntriesSpec: string, expectedCount: int, expectedLastIndex: int64, expectedLastTerm: int64)
    =
    let parse spec =
        if spec = "" then
            []
        else
            spec.Split ','
            |> Array.map (fun s ->
                let parts = s.Split ':'
                createEntry (int64 parts.[0]) (int64 parts.[1]) parts.[2])
            |> Array.toList

    let log = logFromList (parse logSpec)
    let newEntries = parse newEntriesSpec
    let merged = Log.mergeEntries newEntries log
    Assert.Equal(expectedCount, merged.Count)
    Assert.Equal(expectedLastIndex, Log.lastIndex merged)
    Assert.Equal(expectedLastTerm, Log.lastTerm merged)

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
    Assert.Equal(Log.NoOpCommand, trimmed.[2L].Command)
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
    Assert.Equal(Log.NoOpCommand, trimmed.[1L].Command)
