module Raft.Tests.SerializationTests

open Xunit
open Raft

let jsonOptions =
    let opts = System.Text.Json.JsonSerializerOptions()
    opts.Converters.Add(OptionConverterFactory())
    opts.Converters.Add(RaftMessageConverter())
    opts

let roundTrip (msg: RaftMessage) =
    let json = System.Text.Json.JsonSerializer.Serialize(msg, jsonOptions)
    System.Text.Json.JsonSerializer.Deserialize<RaftMessage>(json, jsonOptions)

[<Fact>]
let ``RequestVoteMsg round-trips through JSON`` () =
    let original =
        RequestVoteMsg
            { CandidateTerm = 42L
              CandidateId = 7
              LastLogIndex = 100L
              LastLogTerm = 41L }

    let deserialized = roundTrip original

    match deserialized with
    | RequestVoteMsg rv ->
        Assert.Equal(42L, rv.CandidateTerm)
        Assert.Equal(7, rv.CandidateId)
        Assert.Equal(100L, rv.LastLogIndex)
        Assert.Equal(41L, rv.LastLogTerm)
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``RequestVoteResponseMsg round-trips through JSON`` () =
    let original =
        RequestVoteResponseMsg
            { VoterId = 3
              VoterTerm = 42L
              VoteGranted = true }

    let deserialized = roundTrip original

    match deserialized with
    | RequestVoteResponseMsg rvr ->
        Assert.Equal(3, rvr.VoterId)
        Assert.Equal(42L, rvr.VoterTerm)
        Assert.True rvr.VoteGranted
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``AppendEntriesMsg round-trips through JSON`` () =
    let original =
        AppendEntriesMsg
            { LeaderTerm = 1L
              LeaderId = 2
              PrevLogIndex = 0L
              PrevLogTerm = 0L
              Entries =
                [ { Index = 1L
                    Term = 1L
                    Command = "set x 1" }
                  { Index = 2L
                    Term = 1L
                    Command = "set y 2" } ]
              LeaderCommit = 1L }

    let deserialized = roundTrip original

    match deserialized with
    | AppendEntriesMsg ae ->
        Assert.Equal(1L, ae.LeaderTerm)
        Assert.Equal(2, ae.LeaderId)
        Assert.Equal(2, ae.Entries.Length)
        Assert.Equal("set x 1", ae.Entries[0].Command)
        Assert.Equal("set y 2", ae.Entries[1].Command)
        Assert.Equal(1L, ae.LeaderCommit)
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``AppendEntriesMsg with zero entries round-trips (heartbeat)`` () =
    let original =
        AppendEntriesMsg
            { LeaderTerm = 5L
              LeaderId = 1
              PrevLogIndex = 10L
              PrevLogTerm = 5L
              Entries = []
              LeaderCommit = 10L }

    let deserialized = roundTrip original

    match deserialized with
    | AppendEntriesMsg ae ->
        Assert.Equal(5L, ae.LeaderTerm)
        Assert.Empty ae.Entries
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``AppendEntriesResponseMsg round-trips through JSON`` () =
    let original =
        AppendEntriesResponseMsg
            { FollowerTerm = 1L
              Success = true
              MatchIndex = 5L
              FollowerId = 3
              ConflictTerm = 0L
              ConflictIndex = 0L }

    let deserialized = roundTrip original

    match deserialized with
    | AppendEntriesResponseMsg aer ->
        Assert.Equal(1L, aer.FollowerTerm)
        Assert.True aer.Success
        Assert.Equal(5L, aer.MatchIndex)
        Assert.Equal(3, aer.FollowerId)
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``AppendEntriesResponseMsg with conflict info round-trips`` () =
    let original =
        AppendEntriesResponseMsg
            { FollowerTerm = 2L
              Success = false
              MatchIndex = 0L
              FollowerId = 3
              ConflictTerm = 1L
              ConflictIndex = 2L }

    let deserialized = roundTrip original

    match deserialized with
    | AppendEntriesResponseMsg aer ->
        Assert.False aer.Success
        Assert.Equal(1L, aer.ConflictTerm)
        Assert.Equal(2L, aer.ConflictIndex)
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``InstallSnapshotMsg round-trips through JSON`` () =
    let original =
        InstallSnapshotMsg
            { LeaderTerm = 3L
              LeaderId = 1
              LastIncludedIndex = 50L
              LastIncludedTerm = 3L
              Data = "state-machine-snapshot-data" }

    let deserialized = roundTrip original

    match deserialized with
    | InstallSnapshotMsg snap ->
        Assert.Equal(3L, snap.LeaderTerm)
        Assert.Equal(50L, snap.LastIncludedIndex)
        Assert.Equal(3L, snap.LastIncludedTerm)
        Assert.Equal("state-machine-snapshot-data", snap.Data)
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``InstallSnapshotResponseMsg round-trips through JSON`` () =
    let original =
        InstallSnapshotResponseMsg
            { FollowerTerm = 3L
              FollowerId = 2
              Success = true
              LastIncludedIndex = 50L }

    let deserialized = roundTrip original

    match deserialized with
    | InstallSnapshotResponseMsg snapResp ->
        Assert.Equal(3L, snapResp.FollowerTerm)
        Assert.Equal(2, snapResp.FollowerId)
        Assert.True snapResp.Success
        Assert.Equal(50L, snapResp.LastIncludedIndex)
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``RequestVoteResponseMsg with VoteGranted=false round-trips`` () =
    let original =
        RequestVoteResponseMsg
            { VoterId = 5
              VoterTerm = 10L
              VoteGranted = false }

    let deserialized = roundTrip original

    match deserialized with
    | RequestVoteResponseMsg rvr -> Assert.False rvr.VoteGranted
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``Deserializing invalid case name throws`` () =
    let badJson = """{"Case":"NonExistentMsg","Fields":[]}"""

    let ex =
        Assert.Throws<System.Exception>(fun () ->
            System.Text.Json.JsonSerializer.Deserialize<RaftMessage>(badJson, jsonOptions)
            |> ignore)

    Assert.Contains("Unknown message case", ex.Message)

[<Fact>]
let ``OptionConverter serializes None as null`` () =
    let value: int option = None
    let json = System.Text.Json.JsonSerializer.Serialize(value, jsonOptions)
    Assert.Equal("null", json)

[<Fact>]
let ``OptionConverter deserializes null as None`` () =
    let value =
        System.Text.Json.JsonSerializer.Deserialize<int option>("null", jsonOptions)

    Assert.True value.IsNone

[<Fact>]
let ``OptionConverter round-trips Some value`` () =
    let value: string option = Some "hello"
    let json = System.Text.Json.JsonSerializer.Serialize(value, jsonOptions)

    let deserialized =
        System.Text.Json.JsonSerializer.Deserialize<string option>(json, jsonOptions)

    Assert.Equal(Some "hello", deserialized)

[<Fact>]
let ``OptionConverter round-trips Some numeric value`` () =
    let value: int64 option = Some 42L
    let json = System.Text.Json.JsonSerializer.Serialize(value, jsonOptions)

    let deserialized =
        System.Text.Json.JsonSerializer.Deserialize<int64 option>(json, jsonOptions)

    Assert.Equal(Some 42L, deserialized)

[<Fact>]
let ``OptionConverter round-trips None through JSON`` () =
    let value: int option = None
    let json = System.Text.Json.JsonSerializer.Serialize(value, jsonOptions)

    let deserialized =
        System.Text.Json.JsonSerializer.Deserialize<int option>(json, jsonOptions)

    Assert.Equal(None, deserialized)
