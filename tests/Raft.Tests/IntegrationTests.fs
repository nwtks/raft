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
