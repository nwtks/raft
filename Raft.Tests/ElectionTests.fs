module Raft.Tests.ElectionTests

open Xunit
open Raft

let dummyConfig =
    { NodeId = 1
      Host = "localhost"
      Port = 5001
      Peers =
        [ { Id = 2
            Host = "localhost"
            Port = 5002 }
          { Id = 3
            Host = "localhost"
            Port = 5003 } ]
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500 }

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
                  Log = [] } }

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
let ``Election.handleRequestVote grants vote again when already voted for the same candidate`` () =
    let state =
        { State.init dummyConfig None with
            Persistent =
                { CurrentTerm = 1L
                  VotedFor = Some 2
                  Log = [] } }

    let rv =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let _, resp = Election.handleRequestVote rv state
    Assert.True resp.VoteGranted

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
