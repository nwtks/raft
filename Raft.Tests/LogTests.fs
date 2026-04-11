module Raft.Tests.LogTests

open Xunit
open Raft

let createEntry index term cmd =
    { Index = index
      Term = term
      Command = cmd }

[<Fact>]
let ``empty log has lastIndex 0 and lastTerm 0`` () =
    let log = Log.empty
    Assert.Equal(0L, Log.lastIndex log)
    Assert.Equal(0L, Log.lastTerm log)

[<Fact>]
let ``append adds entry and increments index`` () =
    let log = Log.empty |> Log.append 1L "cmd1"
    Assert.Equal(1L, Log.lastIndex log)
    Assert.Equal(1L, Log.lastTerm log)

    let entry = Log.getEntry 1L log
    Assert.True entry.IsSome
    Assert.Equal("cmd1", entry.Value.Command)

[<Fact>]
let ``mergeEntries handles clean append`` () =
    let log = [ createEntry 1L 1L "cmd1" ]
    let newEntries = [ createEntry 2L 1L "cmd2" ]

    let merged = Log.mergeEntries newEntries log
    Assert.Equal(2, merged.Length)
    Assert.Equal(2L, Log.lastIndex merged)

[<Fact>]
let ``mergeEntries truncates on conflict`` () =
    let log =
        [ createEntry 1L 1L "cmd1"; createEntry 2L 1L "cmd2"; createEntry 3L 1L "cmd3" ]

    let newEntries = [ createEntry 2L 2L "new_cmd2"; createEntry 3L 2L "new_cmd3" ]

    let merged = Log.mergeEntries newEntries log
    Assert.Equal(3, merged.Length)
    Assert.Equal(2L, (Log.getEntry 2L merged).Value.Term)
    Assert.Equal("new_cmd2", (Log.getEntry 2L merged).Value.Command)
