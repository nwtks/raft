module Raft.Tests.ElectionTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``Election.startElection transitions to candidate with incremented term and self-vote`` () =
    let state = State.init dummyConfig None
    Assert.Equal(Follower, state.Role)
    Assert.Equal(0L, state.Persistent.CurrentTerm)

    let newState = Election.startElection state
    Assert.Equal(Candidate, newState.Role)
    Assert.Equal(1L, newState.Persistent.CurrentTerm)
    Assert.Equal(Some 1, newState.Persistent.VotedFor)
    Assert.True(newState.VotesReceived |> Set.contains 1)

[<Fact>]
let ``Election.startElection increments term from non-zero term`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 5L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty
                  LastConfigIndex = 0L } }

    let newState = Election.startElection state
    Assert.Equal(Candidate, newState.Role)
    Assert.Equal(6L, newState.Persistent.CurrentTerm)
    Assert.Equal(Some 1, newState.Persistent.VotedFor)

[<Theory>]
[<InlineData(0L, -1, 1L, 2, 0L, 0L, false, true, 1L, 2)>] // higher term, up-to-date log → grant
[<InlineData(2L, -1, 1L, 2, 0L, 0L, false, false, 2L, -1)>] // lower term → reject
[<InlineData(1L, 2, 1L, 2, 0L, 0L, false, true, 1L, 2)>] // same term, voted same candidate → grant
[<InlineData(1L, -1, 2L, 2, 1L, 0L, true, false, 2L, -1)>] // candidate log behind → reject
[<InlineData(1L, 3, 1L, 2, 0L, 0L, false, false, 1L, 3)>] // voted different candidate → reject
[<InlineData(3L, -1, 3L, 2, 0L, 0L, false, true, 3L, 2)>] // equal term, not voted → grant
[<InlineData(1L, -1, 2L, 2, 1L, 2L, true, true, 2L, 2)>] // candidate log term higher → grant (exercises > short-circuit)
[<InlineData(1L, -1, 1L, 2, 0L, 1L, true, false, 1L, -1)>] // candidate same term but behind → reject (exercises >= false)
let ``Election.handleRequestVote grants vote to candidate with up-to-date log``
    (
        currentTerm: int64,
        votedFor: int,
        candidateTerm: int64,
        candidateId: int,
        lastLogIndex: int64,
        lastLogTerm: int64,
        hasLogEntry: bool,
        expectedGrant: bool,
        expectedTerm: int64,
        expectedVotedFor: int
    ) =
    let votedForOpt = if votedFor = -1 then None else Some votedFor

    let log =
        if hasLogEntry then
            logFromList [ createEntry 1L 1L "x" ]
        else
            Map.empty

    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = currentTerm
                  VotedFor = votedForOpt
                  Log = log
                  Snapshot = None
                  SessionTable = Map.empty
                  LastConfigIndex = 0L } }

    let rv =
        { CandidateTerm = candidateTerm
          CandidateId = candidateId
          LastLogIndex = lastLogIndex
          LastLogTerm = lastLogTerm }

    let newState, resp = Election.handleRequestVote rv state
    Assert.Equal(expectedGrant, resp.VoteGranted)
    Assert.Equal(expectedTerm, resp.VoterTerm)

    let expectedVotedForOpt =
        if expectedVotedFor = -1 then
            None
        else
            Some expectedVotedFor

    Assert.Equal(expectedVotedForOpt, newState.Persistent.VotedFor)

[<Fact>]
let ``Election.handleVoteResponse promotes to leader on majority votes`` () =
    let state = Election.startElection (State.init dummyConfig None)

    let resp =
        { VoterId = 2
          VoterTerm = 1L
          VoteGranted = true }

    let newState = Election.handleVoteResponse 2 resp state
    Assert.Equal(Leader, newState.Role)
    Assert.True newState.LeaderState.IsSome
    Assert.Equal(Some 1, newState.CurrentLeader)

[<Fact>]
let ``Election.handleVoteResponse records vote but stays candidate without majority`` () =
    let config5Nodes =
        { dummyConfig with
            Peers =
                [ { Id = 2; Host = ""; Port = 0 }
                  { Id = 3; Host = ""; Port = 0 }
                  { Id = 4; Host = ""; Port = 0 }
                  { Id = 5; Host = ""; Port = 0 } ] }

    let state = Election.startElection (State.init config5Nodes None)

    let resp =
        { VoterId = 2
          VoterTerm = 1L
          VoteGranted = true }

    let newState = Election.handleVoteResponse 2 resp state
    Assert.Equal(Candidate, newState.Role)
    Assert.Equal(2, newState.VotesReceived.Count)
    Assert.True(newState.VotesReceived.Contains 1)
    Assert.True(newState.VotesReceived.Contains 2)
    Assert.True newState.LeaderState.IsNone

[<Fact>]
let ``Election.handleVoteResponse updates term on response with higher term`` () =
    let state = Election.startElection (State.init dummyConfig None)

    let resp =
        { VoterId = 2
          VoterTerm = 2L
          VoteGranted = false }

    let newState = Election.handleVoteResponse 2 resp state
    Assert.Equal(2L, newState.Persistent.CurrentTerm)
    Assert.Equal(None, newState.Persistent.VotedFor)

[<Fact>]
let ``Election.handleVoteResponse ignores denied vote when staying candidate without majority`` () =
    let config5Nodes =
        { dummyConfig with
            Peers =
                [ { Id = 2; Host = ""; Port = 0 }
                  { Id = 3; Host = ""; Port = 0 }
                  { Id = 4; Host = ""; Port = 0 }
                  { Id = 5; Host = ""; Port = 0 } ] }

    let state = Election.startElection (State.init config5Nodes None)

    let resp =
        { VoterId = 2
          VoterTerm = 1L
          VoteGranted = false }

    let newState = Election.handleVoteResponse 2 resp state
    Assert.Equal(Candidate, newState.Role)
    Assert.Equal(1, newState.VotesReceived.Count)

[<Fact>]
let ``Election.handleVoteResponse ignores duplicate vote from already-voted node`` () =
    let state = Election.startElection (State.init dummyConfig None)

    let resp =
        { VoterId = 2
          VoterTerm = 1L
          VoteGranted = true }

    let afterFirst = Election.handleVoteResponse 2 resp state
    Assert.Equal(Leader, afterFirst.Role)

    let afterDup = Election.handleVoteResponse 2 resp afterFirst
    Assert.Equal(Leader, afterDup.Role)
    Assert.Equal(afterFirst, afterDup)

[<Fact>]
let ``Election.handleVoteResponse is no-op when node is follower (not candidate)`` () =
    let state = State.init dummyConfig None

    let resp =
        { VoterId = 2
          VoterTerm = 0L
          VoteGranted = true }

    let newState = Election.handleVoteResponse 2 resp state
    Assert.Equal(Follower, newState.Role)
    Assert.Equal(state, newState)
