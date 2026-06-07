module Raft.Tests.ElectionTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``Election.startElection transitions node to Candidate, increments term, and votes for self`` () =
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
                  SessionTable = Map.empty } }

    let newState = Election.startElection state
    Assert.Equal(Candidate, newState.Role)
    Assert.Equal(6L, newState.Persistent.CurrentTerm)
    Assert.Equal(Some 1, newState.Persistent.VotedFor)

[<Fact>]
let ``Election.handleRequestVote grants vote when candidate term is higher and log is up-to-date`` () =
    let state = State.init dummyConfig None

    let rv =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let newState, resp = Election.handleRequestVote rv state
    Assert.True resp.VoteGranted
    Assert.Equal(1L, resp.VoterTerm)
    Assert.Equal(Some 2, newState.Persistent.VotedFor)

[<Fact>]
let ``Election.handleRequestVote rejects vote when candidate term is lower than current term`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty } }

    let rv =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let newState, resp = Election.handleRequestVote rv state
    Assert.False resp.VoteGranted
    Assert.Equal(2L, resp.VoterTerm)
    Assert.Equal(None, newState.Persistent.VotedFor)

[<Fact>]
let ``Election.handleRequestVote grants vote again when already voted for the same candidate`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = Some 2
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty } }

    let rv =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let _, resp = Election.handleRequestVote rv state
    Assert.True resp.VoteGranted

[<Fact>]
let ``Election.handleRequestVote rejects when candidate log is behind`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = None
                  Log =
                    logFromList
                        [ { Index = 1L
                            Term = 1L
                            Command = "x"
                            ClientId = None
                            SeqNum = None } ]
                  Snapshot = None
                  SessionTable = Map.empty } }

    let rv =
        { CandidateTerm = 2L
          CandidateId = 2
          LastLogIndex = 1L
          LastLogTerm = 0L }

    let _, resp = Election.handleRequestVote rv state
    Assert.False resp.VoteGranted

[<Fact>]
let ``Election.handleRequestVote rejects when already voted for different candidate in same term`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = Some 3
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty } }

    let rv =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let _, resp = Election.handleRequestVote rv state
    Assert.False resp.VoteGranted
    Assert.Equal(1L, resp.VoterTerm)

[<Fact>]
let ``Election.handleRequestVote grants vote when candidate term equals current term and not voted`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 3L
                  VotedFor = None
                  Log = Map.empty
                  Snapshot = None
                  SessionTable = Map.empty } }

    let rv =
        { CandidateTerm = 3L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let _, resp = Election.handleRequestVote rv state
    Assert.True resp.VoteGranted
    Assert.Equal(3L, resp.VoterTerm)

[<Fact>]
let ``Election.handleVoteResponse updates current term when response carries a higher term`` () =
    let state = Election.startElection (State.init dummyConfig None)

    let resp =
        { VoterId = 2
          VoterTerm = 2L
          VoteGranted = false }

    let newState = Election.handleVoteResponse 2 resp state
    Assert.Equal(2L, newState.Persistent.CurrentTerm)
    Assert.Equal(None, newState.Persistent.VotedFor)

[<Fact>]
let ``Election.handleVoteResponse records vote but stays Candidate without majority`` () =
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
    Assert.True(newState.LeaderState.IsNone)

[<Fact>]
let ``Election.handleVoteResponse promotes node to Leader upon receiving majority votes`` () =
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
let ``Election.handleVoteResponse is no-op when node is Follower (not Candidate)`` () =
    let state = State.init dummyConfig None

    let resp =
        { VoterId = 2
          VoterTerm = 0L
          VoteGranted = true }

    let newState = Election.handleVoteResponse 2 resp state
    Assert.Equal(Follower, newState.Role)
    Assert.Equal(state, newState)

[<Fact>]
let ``Election.handleVoteResponse ignores denied vote when staying Candidate without majority`` () =
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
