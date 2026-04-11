module Raft.Tests.IntegrationTests

open Xunit
open Raft

// Simulate simple cluster by directly passing messages (no TCP).
[<Fact>]
let ``Integration: 3 nodes elect leader and commit command`` () =
    let config1 =
        { NodeId = 1
          Host = ""
          Port = 0
          Peers = [ { Id = 2; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1 }

    let config2 =
        { config1 with
            NodeId = 2
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ] }

    let config3 =
        { config1 with
            NodeId = 3
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 2; Host = ""; Port = 0 } ] }

    let mutable s1 = State.init config1 None
    let mutable s2 = State.init config2 None
    let mutable s3 = State.init config3 None

    // 1. Node 1 times out and starts election
    s1 <- Election.startElection s1
    let rv1 = Election.createRequestVote s1

    // 2. Nodes 2 and 3 receive RequestVote
    let s2_new, resp2 = Election.handleRequestVote rv1 s2
    s2 <- s2_new
    let s3_new, resp3 = Election.handleRequestVote rv1 s3
    s3 <- s3_new

    Assert.True resp2.VoteGranted
    Assert.True resp3.VoteGranted

    // 3. Node 1 receives responses and becomes leader
    s1 <- Election.handleVoteResponse 2 resp2 s1
    s1 <- Election.handleVoteResponse 3 resp3 s1

    Assert.Equal(Leader, s1.Role)
    Assert.True s1.LeaderState.IsSome

    // 4. Client submits command to leader
    s1 <- Replication.appendCommand "put a 10" s1

    Assert.Equal(1, s1.Persistent.Log.Length)

    // 5. Leader sends AppendEntries
    let ae_to2 = Replication.createAppendEntries 2 s1
    let ae_to3 = Replication.createAppendEntries 3 s1

    // 6. Followers handle AppendEntries
    let s2_after_ae, resp2_ae = Replication.handleAppendEntries ae_to2.Value s2
    s2 <- s2_after_ae
    let s3_after_ae, resp3_ae = Replication.handleAppendEntries ae_to3.Value s3
    s3 <- s3_after_ae

    Assert.True resp2_ae.Success
    Assert.Equal(1, s2.Persistent.Log.Length)

    // 7. Leader handles responses and advances commit index
    s1 <- Replication.handleAppendEntriesResponse resp2_ae s1
    s1 <- Replication.handleAppendEntriesResponse resp3_ae s1
    s1 <- Replication.advanceCommitIndex s1

    Assert.Equal(1L, s1.Volatile.CommitIndex)

    // 8. Leader sends Heartbeat to update follower commit index
    let hb_to2 = Replication.createHeartbeat 2 s1
    let s2_final, _ = Replication.handleAppendEntries hb_to2.Value s2
    s2 <- s2_final

    Assert.Equal(1L, s2.Volatile.CommitIndex)

[<Fact>]
let ``Integration: leader down causes new election and leader change`` () =
    let config1 =
        { NodeId = 1
          Host = ""
          Port = 0
          Peers = [ { Id = 2; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1 }

    let config2 =
        { config1 with
            NodeId = 2
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ] }

    let config3 =
        { config1 with
            NodeId = 3
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 2; Host = ""; Port = 0 } ] }

    let mutable s1 = State.init config1 None
    let mutable s2 = State.init config2 None
    let mutable s3 = State.init config3 None

    // 1. Initial election: Node 1 becomes leader
    s1 <- Election.startElection s1
    let rv1 = Election.createRequestVote s1

    let s2_new, resp2 = Election.handleRequestVote rv1 s2
    s2 <- s2_new
    let s3_new, resp3 = Election.handleRequestVote rv1 s3
    s3 <- s3_new

    s1 <- Election.handleVoteResponse 2 resp2 s1
    s1 <- Election.handleVoteResponse 3 resp3 s1

    Assert.Equal(Leader, s1.Role)
    Assert.Equal(1L, s1.Persistent.CurrentTerm)

    // 2. Leader (Node 1) goes down (stops sending heartbeats).
    // Node 2 times out and starts a new election.
    s2 <- Election.startElection s2

    Assert.Equal(Candidate, s2.Role)
    Assert.Equal(2L, s2.Persistent.CurrentTerm)

    let rv2 = Election.createRequestVote s2

    // 3. Node 3 receives RequestVote from Node 2
    let s3_newer, resp3_2 = Election.handleRequestVote rv2 s3
    s3 <- s3_newer

    Assert.True resp3_2.VoteGranted
    Assert.Equal(2L, s3.Persistent.CurrentTerm)

    // 4. Node 2 receives response from Node 3 and becomes new leader
    s2 <- Election.handleVoteResponse 3 resp3_2 s2

    Assert.Equal(Leader, s2.Role)
    Assert.True s2.LeaderState.IsSome
    Assert.Equal(2L, s2.Persistent.CurrentTerm)

    // 5. Original Leader (Node 1) comes back online.
    // It still thinks it is the leader of Term 1.
    Assert.Equal(Leader, s1.Role)
    Assert.Equal(1L, s1.Persistent.CurrentTerm)

    // 6. New Leader (Node 2) sends a heartbeat to Node 1.
    let hb_from2 = Replication.createHeartbeat 1 s2

    Assert.True hb_from2.IsSome
    Assert.Equal(2L, hb_from2.Value.LeaderTerm)

    // 7. Node 1 receives the heartbeat from Node 2.
    // Since LeaderTerm (2) > CurrentTerm (1), Node 1 must step down.
    let s1_after_hb, resp1_ae = Replication.handleAppendEntries hb_from2.Value s1
    s1 <- s1_after_hb

    Assert.Equal(Follower, s1.Role)
    Assert.Equal(2L, s1.Persistent.CurrentTerm)
    Assert.Equal(Some 2, s1.CurrentLeader)
    Assert.True resp1_ae.Success

[<Fact>]
let ``Integration: two nodes become candidates and one wins`` () =
    let config1 =
        { NodeId = 1
          Host = ""
          Port = 0
          Peers = [ { Id = 2; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1 }

    let config2 =
        { config1 with
            NodeId = 2
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ] }

    let config3 =
        { config1 with
            NodeId = 3
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 2; Host = ""; Port = 0 } ] }

    let mutable s1 = State.init config1 None
    let mutable s2 = State.init config2 None
    let mutable s3 = State.init config3 None

    // 1. Node 1 and Node 2 both start election for Term 1
    s1 <- Election.startElection s1
    s2 <- Election.startElection s2

    Assert.Equal(Candidate, s1.Role)
    Assert.Equal(Candidate, s2.Role)
    Assert.Equal(1L, s1.Persistent.CurrentTerm)
    Assert.Equal(1L, s2.Persistent.CurrentTerm)

    // 2. Node 3 receives RequestVote from Node 1 first and grants it
    let rv1 = Election.createRequestVote s1
    let s3_new, resp3 = Election.handleRequestVote rv1 s3
    s3 <- s3_new

    Assert.True resp3.VoteGranted

    // 3. Node 2 receives RequestVote from Node 1.
    // Since s2 already voted for itself in Term 1, it rejects rv1.
    let s2_after_rv, resp2_rv = Election.handleRequestVote rv1 s2
    s2 <- s2_after_rv

    Assert.False resp2_rv.VoteGranted

    // 4. Node 1 receives vote from Node 3 and becomes Leader (Self + Node 3)
    s1 <- Election.handleVoteResponse 3 resp3 s1

    Assert.Equal(Leader, s1.Role)

    // 5. Node 1 sends AppendEntries/Heartbeat to Node 2 (who is still a Candidate)
    let hb_from1 = Replication.createHeartbeat 2 s1

    Assert.True hb_from1.IsSome

    // 6. Node 2 receives AppendEntries from a valid leader of the same term and steps down
    let s2_final, resp2_ae = Replication.handleAppendEntries hb_from1.Value s2
    s2 <- s2_final

    Assert.Equal(Follower, s2.Role)
    Assert.Equal(1L, s2.Persistent.CurrentTerm)
    Assert.Equal(Some 1, s2.CurrentLeader)
    Assert.True resp2_ae.Success

[<Fact>]
let ``Integration: stale leader is rejected and steps down`` () =
    // Reuse basic config
    let config1 =
        { NodeId = 1
          Host = ""
          Port = 0
          Peers = [ { Id = 2; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1 }

    let config2 =
        { config1 with
            NodeId = 2
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ] }

    let config3 =
        { config1 with
            NodeId = 3
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 2; Host = ""; Port = 0 } ] }

    let mutable s1 = State.init config1 None
    let mutable s2 = State.init config2 None
    let mutable s3 = State.init config3 None

    // 1. Node 1 becomes Leader of Term 1
    s1 <- Election.startElection s1
    let rv1 = Election.createRequestVote s1
    let s2_new, resp2 = Election.handleRequestVote rv1 s2
    s2 <- s2_new
    s1 <- Election.handleVoteResponse 2 resp2 s1

    Assert.Equal(Leader, s1.Role)
    Assert.Equal(1L, s1.Persistent.CurrentTerm)

    // 2. Node 1 is partitioned. Node 2 and Node 3 elect Node 2 as Leader of Term 2.
    // (Note: Node 1 still thinks it is Leader of Term 1)
    s2 <- Election.startElection s2
    let rv2 = Election.createRequestVote s2
    let s3_new, resp3 = Election.handleRequestVote rv2 s3
    s3 <- s3_new
    s2 <- Election.handleVoteResponse 3 resp3 s2

    Assert.Equal(Leader, s2.Role)
    Assert.Equal(2L, s2.Persistent.CurrentTerm)
    Assert.Equal(Leader, s1.Role)
    Assert.Equal(1L, s1.Persistent.CurrentTerm)

    // 3. Stale Leader (Node 1) attempts to send AppendEntries to Node 2
    let ae_from1 = Replication.createHeartbeat 2 s1

    Assert.True ae_from1.IsSome
    Assert.Equal(1L, ae_from1.Value.LeaderTerm)

    // 4. Node 2 receives AppendEntries. Since LeaderTerm (1) < CurrentTerm (2), it must reject it.
    let s2_after, resp2_ae = Replication.handleAppendEntries ae_from1.Value s2
    s2 <- s2_after

    Assert.False resp2_ae.Success
    Assert.Equal(2L, resp2_ae.FollowerTerm)
    Assert.Equal(Leader, s2.Role)

    // 5. Node 1 receives the response.
    // Since FollowerTerm (2) > CurrentTerm (1), Node 1 must step down to Follower.
    s1 <- Replication.handleAppendEntriesResponse resp2_ae s1

    Assert.Equal(Follower, s1.Role)
    Assert.Equal(2L, s1.Persistent.CurrentTerm)
    Assert.True s1.LeaderState.IsNone

[<Fact>]
let ``Integration: leader recovers from log inconsistency by decrementing nextIndex`` () =
    let config1 =
        { NodeId = 1
          Host = ""
          Port = 0
          Peers = [ { Id = 2; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1 }

    let config2 =
        { config1 with
            NodeId = 2
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ] }

    let mutable s1 = State.init config1 None
    let mutable s2 = State.init config2 None

    // 1. Initial state: both have index 1, term 1
    let entry1 = { Index = 1L; Term = 1L; Command = "A" }

    s1 <-
        { s1 with
            Persistent =
                { s1.Persistent with
                    CurrentTerm = 3L
                    Log = [ entry1 ] } }

    s2 <-
        { s2 with
            Persistent =
                { s2.Persistent with
                    CurrentTerm = 3L
                    Log = [ entry1 ] } }

    // 2. LOG INCONSISTENCY:
    // Node 1 (Leader) has [1,1,A]; [2,1,B]
    // Node 2 (Follower) has [1,1,A]; [2,2,C] (different term at index 2)
    let entry1B = { Index = 2L; Term = 1L; Command = "B" }
    let entry2C = { Index = 2L; Term = 2L; Command = "C" }

    s1 <-
        { s1 with
            Role = Leader
            Persistent =
                { s1.Persistent with
                    Log = [ entry1; entry1B ] }
            LeaderState =
                Some
                    { NextIndex = Map.ofList [ 2, 3L ]
                      MatchIndex = Map.ofList [ 2, 0L ] } }

    s2 <-
        { s2 with
            Persistent =
                { s2.Persistent with
                    Log = [ entry1; entry2C ] } }

    // 3. Leader adds a new command at index 3
    s1 <- Replication.appendCommand "D" s1

    Assert.Equal(3, s1.Persistent.Log.Length)

    // 4. Leader tries to send AppendEntries for index 3.
    // PrevLogIndex = 2, PrevLogTerm = 1 (Node 1's term at index 2)
    let ae1 = Replication.createAppendEntries 2 s1

    Assert.True ae1.IsSome
    Assert.Equal(2L, ae1.Value.PrevLogIndex)
    Assert.Equal(1L, ae1.Value.PrevLogTerm)

    // 5. Node 2 receives it.
    // Node 2 has Term 2 at index 2. Since 1 <> 2, it rejects.
    let s2_after_failed, resp_fail = Replication.handleAppendEntries ae1.Value s2
    s2 <- s2_after_failed

    Assert.False resp_fail.Success

    // 6. Leader handles failure and decrements nextIndex
    s1 <- Replication.handleAppendEntriesResponse resp_fail s1
    let nextIdx = s1.LeaderState.Value.NextIndex.[2]

    Assert.Equal(2L, nextIdx)

    // 7. Leader retries AppendEntries starting from index 2
    // PrevLogIndex = 1, PrevLogTerm = 1
    let ae2 = Replication.createAppendEntries 2 s1

    Assert.True ae2.IsSome
    Assert.Equal(1L, ae2.Value.PrevLogIndex)
    Assert.Equal(1L, ae2.Value.PrevLogTerm)
    Assert.Equal(2, ae2.Value.Entries.Length) // Sends [2,1,B] and [3,3,D]

    // 8. Node 2 receives it. Node 2 has Term 1 at index 1. It matches!
    // Node 2 overwrites its conflicting index 2 and appends index 3.
    let s2_final, resp_success = Replication.handleAppendEntries ae2.Value s2
    s2 <- s2_final

    Assert.True resp_success.Success
    Assert.Equal(3L, resp_success.MatchIndex)
    Assert.Equal(3, s2.Persistent.Log.Length)
    Assert.Equal("B", s2.Persistent.Log.[1].Command)
    Assert.Equal("D", s2.Persistent.Log.[2].Command)

    // 9. Leader finalizes match index
    s1 <- Replication.handleAppendEntriesResponse resp_success s1

    Assert.Equal(3L, s1.LeaderState.Value.MatchIndex.[2])
