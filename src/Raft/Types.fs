namespace Raft

// ---------------------------------------------------------------
// Core types for the Raft consensus algorithm
// ---------------------------------------------------------------

type NodeId = int

type Term = int64

type LogIndex = int64

type LogEntry =
    { Index: LogIndex
      Term: Term
      Command: string }

type NodeRole =
    | Follower
    | Candidate
    | Leader

// ---------------------------------------------------------------
// RPC Messages
// ---------------------------------------------------------------

type RequestVote =
    { CandidateTerm: Term
      CandidateId: NodeId
      LastLogIndex: LogIndex
      LastLogTerm: Term }

type RequestVoteResponse =
    { VoterId: NodeId
      VoterTerm: Term
      VoteGranted: bool }

type AppendEntries =
    { LeaderTerm: Term
      LeaderId: NodeId
      PrevLogIndex: LogIndex
      PrevLogTerm: Term
      Entries: LogEntry list
      LeaderCommit: LogIndex }

type AppendEntriesResponse =
    { FollowerTerm: Term
      Success: bool
      MatchIndex: LogIndex
      FollowerId: NodeId }

type RaftMessage =
    | RequestVoteMsg of RequestVote
    | RequestVoteResponseMsg of RequestVoteResponse
    | AppendEntriesMsg of AppendEntries
    | AppendEntriesResponseMsg of AppendEntriesResponse
    | ClientCommand of command: string * AsyncReplyChannel<bool> option

// ---------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------

type PeerInfo = { Id: NodeId; Host: string; Port: int }

type NodeConfig =
    { NodeId: NodeId
      Host: string
      Port: int
      Peers: PeerInfo list
      ElectionTimeoutMinMs: int
      ElectionTimeoutMaxMs: int
      HeartbeatIntervalMs: int }
