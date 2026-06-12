module Raft.Tests.IntegrationTests

open Xunit
open Raft
open TestHelpers

// Simulate simple cluster by directly passing messages (no TCP).
[<Fact>]
let ``3-node cluster elects a leader and commits a client command`` () =
    let c1, c2, c3 = threeNodeConfigs ()
    let mutable s1 = State.init c1 None
    let mutable s2 = State.init c2 None
    let mutable s3 = State.init c3 None

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
    // Log contains [noop@1 (from initLeaderState), cmd@2]
    Assert.Equal(2, s1.Persistent.Log.Count)

    // 5. Leader sends AppendEntries
    let ae_to2 = Replication.createAppendEntries 2 s1
    let ae_to3 = Replication.createAppendEntries 3 s1

    // 6. Followers handle AppendEntries
    let s2_after_ae, resp2_ae = Replication.handleAppendEntries ae_to2.Value s2
    s2 <- s2_after_ae
    let s3_after_ae, resp3_ae = Replication.handleAppendEntries ae_to3.Value s3
    s3 <- s3_after_ae

    Assert.True resp2_ae.Success
    Assert.Equal(2, s2.Persistent.Log.Count)

    // 7. Leader handles responses and advances commit index
    s1 <- Replication.handleAppendEntriesResponse resp2_ae s1
    s1 <- Replication.handleAppendEntriesResponse resp3_ae s1
    s1 <- Replication.advanceCommitIndex s1

    Assert.Equal(2L, s1.Volatile.CommitIndex)

    // 8. Leader sends Heartbeat to update follower commit index
    let hb_to2 = Replication.createHeartbeat 2 s1
    let s2_final, _ = Replication.handleAppendEntries hb_to2.Value s2
    s2 <- s2_final

    Assert.Equal(2L, s2.Volatile.CommitIndex)

[<Fact>]
let ``Leader failure triggers new election and leadership change in 3-node cluster`` () =
    let c1, c2, c3 = threeNodeConfigs ()
    let mutable s1 = State.init c1 None
    let mutable s2 = State.init c2 None
    let mutable s3 = State.init c3 None

    // 1. Initial election: Node 1 becomes leader
    let s1_new, s2_new, s3_new, _ = electLeader s1 s2 s3
    s1 <- s1_new
    s2 <- s2_new
    s3 <- s3_new

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
let ``Concurrent candidacy resolves with one leader in 3-node cluster`` () =
    let c1, c2, c3 = threeNodeConfigs ()
    let mutable s1 = State.init c1 None
    let mutable s2 = State.init c2 None
    let mutable s3 = State.init c3 None

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
let ``Stale leader AppendEntries is rejected and stale leader steps down to follower`` () =
    let c1, c2, c3 = threeNodeConfigs ()
    let mutable s1 = State.init c1 None
    let mutable s2 = State.init c2 None
    let mutable s3 = State.init c3 None

    // 1. Node 1 becomes Leader of Term 1 (only Node 2 responds; Node 3 is simulated as partitioned later)
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
let ``Leader resolves log inconsistency by decrementing NextIndex and retrying AppendEntries`` () =
    let config1 =
        { NodeId = 1
          Host = ""
          Port = 0
          Peers = [ { Id = 2; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1
          SnapshotAutoThreshold = 0 }

    let config2 =
        { config1 with
            NodeId = 2
            Peers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 3; Host = ""; Port = 0 } ] }

    let mutable s1 = State.init config1 None
    let mutable s2 = State.init config2 None

    // 1. Initial state: both have index 1, term 1
    let entry1 =
        { Index = 1L
          Term = 1L
          Command = "A"
          ClientId = None
          SeqNum = None }

    s1 <-
        { s1 with
            Persistent =
                { s1.Persistent with
                    CurrentTerm = 3L
                    Log = logFromList [ entry1 ] } }

    s2 <-
        { s2 with
            Persistent =
                { s2.Persistent with
                    CurrentTerm = 3L
                    Log = logFromList [ entry1 ] } }

    // 2. LOG INCONSISTENCY:
    // Node 1 (Leader) has [1,1,A]; [2,1,B]
    // Node 2 (Follower) has [1,1,A]; [2,2,C] (different term at index 2)
    let entry1B =
        { Index = 2L
          Term = 1L
          Command = "B"
          ClientId = None
          SeqNum = None }

    let entry2C =
        { Index = 2L
          Term = 2L
          Command = "C"
          ClientId = None
          SeqNum = None }

    s1 <-
        { s1 with
            Role = Leader
            Persistent =
                { s1.Persistent with
                    Log = logFromList [ entry1; entry1B ] }
            LeaderState =
                Some
                    { NextIndex = Map.ofList [ 2, 3L ]
                      MatchIndex = Map.ofList [ 2, 0L ] } }

    s2 <-
        { s2 with
            Persistent =
                { s2.Persistent with
                    Log = logFromList [ entry1; entry2C ] } }

    // 3. Leader adds a new command at index 3
    s1 <- Replication.appendCommand "D" s1

    Assert.Equal(3, s1.Persistent.Log.Count)

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
    Assert.Equal(3, s2.Persistent.Log.Count)
    Assert.Equal("B", (Map.find 2L s2.Persistent.Log).Command)
    Assert.Equal("D", (Map.find 3L s2.Persistent.Log).Command)

    // 9. Leader finalizes match index
    s1 <- Replication.handleAppendEntriesResponse resp_success s1

    Assert.Equal(3L, s1.LeaderState.Value.MatchIndex.[2])

[<Fact>]
let ``Leader replicates commands to followers who both commit and apply`` () =
    let c1, c2, c3 = threeNodeConfigs ()
    let mutable s1 = State.init c1 None
    let mutable s2 = State.init c2 None
    let mutable s3 = State.init c3 None

    // 1. Node 1 becomes leader of Term 1
    let s1_new, s2_new, s3_new, _ = electLeader s1 s2 s3
    s1 <- s1_new
    s2 <- s2_new
    s3 <- s3_new
    Assert.Equal(Leader, s1.Role)

    // 2. Leader appends two commands (noop@1 from initLeaderState, then cmd1@2, cmd2@3)
    s1 <- Replication.appendCommand "cmd1" s1
    s1 <- Replication.appendCommand "cmd2" s1
    Assert.Equal(3, s1.Persistent.Log.Count)

    // 3. Replicate to Node 2 (NextIndex[2]=1, so AE sends noop, cmd1, cmd2)
    let ae1 = (Replication.createAppendEntries 2 s1).Value
    let r4 = Replication.handleAppendEntries ae1 s2
    s2 <- fst r4
    let resp_ae = snd r4
    Assert.True resp_ae.Success
    Assert.Equal(3L, resp_ae.MatchIndex) // all 3 entries matched
    s1 <- Replication.handleAppendEntriesResponse resp_ae s1
    s1 <- Replication.advanceCommitIndex s1
    // Leader + Peer2 matched index 3 (2/3 = majority), so commit advances to 3
    Assert.Equal(3L, s1.Volatile.CommitIndex)

    // 4. Replicate to Node 3 (NextIndex[3] still 1, sends all entries)
    let ae2 = (Replication.createAppendEntries 3 s1).Value
    let r5 = Replication.handleAppendEntries ae2 s3
    s3 <- fst r5
    let resp3_ae = snd r5
    Assert.True resp3_ae.Success
    Assert.Equal(3L, resp3_ae.MatchIndex)
    s1 <- Replication.handleAppendEntriesResponse resp3_ae s1
    s1 <- Replication.advanceCommitIndex s1
    // All 3 matched index 3, commit index stays at 3
    Assert.Equal(3L, s1.Volatile.CommitIndex)

    // 5. Apply committed on leader (noop@1 is skipped, cmd1@2 and cmd2@3 are applied)
    let applied = ResizeArray<string>()
    s1 <- NodeApply.applyCommitted (fun e -> applied.Add e.Command) s1
    Assert.Equal(2, applied.Count)
    Assert.Contains("cmd1", applied)
    Assert.Contains("cmd2", applied)
    Assert.Equal(3L, s1.Volatile.LastApplied)

    // 6. Send heartbeat to followers so they commit too
    let hb_to2 = (Replication.createHeartbeat 2 s1).Value
    let hb_to3 = (Replication.createHeartbeat 3 s1).Value
    let r6 = Replication.handleAppendEntries hb_to2 s2
    s2 <- fst r6
    let r7 = Replication.handleAppendEntries hb_to3 s3
    s3 <- fst r7

    Assert.Equal(3L, s2.Volatile.CommitIndex)
    Assert.Equal(3L, s3.Volatile.CommitIndex)

    s2 <- NodeApply.applyCommitted (fun _ -> ()) s2
    s3 <- NodeApply.applyCommitted (fun _ -> ()) s3
    Assert.Equal(3L, s2.Volatile.LastApplied)
    Assert.Equal(3L, s3.Volatile.LastApplied)

[<Fact>]
let ``takeSnapshot on leader trims log and installs snapshot on follower`` () =
    let config1 =
        { NodeId = 1
          Host = ""
          Port = 0
          Peers = [ { Id = 2; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1
          SnapshotAutoThreshold = 0 }

    let config2 =
        { NodeId = 2
          Host = ""
          Port = 0
          Peers = [ { Id = 1; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1
          SnapshotAutoThreshold = 0 }

    let mutable s1 = State.init config1 None
    let mutable s2 = State.init config2 None

    // 1. Node 1 becomes leader of Term 1
    s1 <- Election.startElection s1
    let rv1 = Election.createRequestVote s1
    let r2 = Election.handleRequestVote rv1 s2
    s2 <- fst r2
    let resp2 = snd r2
    s1 <- Election.handleVoteResponse 2 resp2 s1
    Assert.Equal(Leader, s1.Role)

    // 2. Append and commit 3 commands
    for cmd in [ "a"; "b"; "c" ] do
        s1 <- Replication.appendCommand cmd s1
        let ae = (Replication.createAppendEntries 2 s1).Value
        let r = Replication.handleAppendEntries ae s2
        s2 <- fst r
        let resp_ae = snd r
        s1 <- Replication.handleAppendEntriesResponse resp_ae s1
        s1 <- Replication.advanceCommitIndex s1

    // With noop@1, the 3 commands occupy indices 2, 3, 4
    Assert.Equal(4L, s1.Volatile.CommitIndex)

    // 3. Apply committed on leader (noop is skipped)
    s1 <- NodeApply.applyCommitted (fun _ -> ()) s1
    Assert.Equal(4L, s1.Volatile.LastApplied)

    // 4. Take snapshot at index 2 on leader
    s1 <- State.takeSnapshot 2L 1L "snap-data" s1
    Assert.True s1.Persistent.Snapshot.IsSome
    Assert.Equal(2L, s1.Persistent.Snapshot.Value.LastIncludedIndex)
    // Log should be trimmed: entries at 1 and 2 removed, sentinel at 2, entry 3 remains
    Assert.False(s1.Persistent.Log.ContainsKey 1L)
    Assert.True(s1.Persistent.Log.ContainsKey 2L) // sentinel
    Assert.True(s1.Persistent.Log.ContainsKey 3L) // not trimmed

    // 5. Now simulate InstallSnapshot from leader (1) to follower (2)
    let snapMsg: InstallSnapshot =
        { LeaderTerm = s1.Persistent.CurrentTerm
          LeaderId = 1
          LastIncludedIndex = s1.Persistent.Snapshot.Value.LastIncludedIndex
          LastIncludedTerm = s1.Persistent.Snapshot.Value.LastIncludedTerm
          Data = s1.Persistent.Snapshot.Value.StateMachineData }

    let r3 = Replication.handleInstallSnapshot snapMsg s2
    s2 <- fst r3
    let snapResp = snd r3
    Assert.True snapResp.Success
    Assert.Equal(2L, snapResp.LastIncludedIndex)

    // Follower should have the snapshot installed and log trimmed
    Assert.True s2.Persistent.Snapshot.IsSome
    Assert.Equal("snap-data", s2.Persistent.Snapshot.Value.StateMachineData)
    Assert.False(s2.Persistent.Log.ContainsKey 1L)
    Assert.Equal(Log.NoOpCommand, s2.Persistent.Log.[2L].Command) // sentinel

    // 6. Leader handles the response
    let s1_after_snapResp = Replication.handleInstallSnapshotResponse snapResp s1
    Assert.Equal(2L, s1_after_snapResp.LeaderState.Value.MatchIndex.[2])
    Assert.Equal(3L, s1_after_snapResp.LeaderState.Value.NextIndex.[2])

[<Fact>]
let ``broadcastAppendEntries falls back to InstallSnapshot when follower is behind snapshot`` () =
    // This simulates the scenario in NodeBroadcaster.broadcastAppendEntries
    // where createAppendEntries returns None (follower behind snapshot)
    // and createInstallSnapshot returns Some

    let log =
        logFromList
            [ { Index = 5L
                Term = 2L
                Command = "d"
                ClientId = None
                SeqNum = None } ]

    let ls: LeaderState =
        { NextIndex = Map.ofList [ 2, 1L ]
          MatchIndex = Map.ofList [ 2, 0L ] }

    let state =
        { State.init dummyConfig None with
            Role = Leader
            Persistent =
                { CurrentTerm = 2L
                  VotedFor = None
                  Log = log
                  Snapshot =
                    Some
                        { LastIncludedIndex = 3L
                          LastIncludedTerm = 1L
                          StateMachineData = "snap" }
                  SessionTable = Map.empty
                  LastConfigIndex = 0L }
            LeaderState = Some ls
            Volatile = { CommitIndex = 3L; LastApplied = 3L } }

    // createAppendEntries should return None (prevLogIndex=0 < snap.LastIncludedIndex=3)
    let ae = Replication.createAppendEntries 2 state
    Assert.True ae.IsNone

    // createInstallSnapshot should return Some (nextIdx=1 <= 3+1)
    let snap = Replication.createInstallSnapshot 2 state
    Assert.True snap.IsSome
    Assert.Equal(3L, snap.Value.LastIncludedIndex)
    Assert.Equal("snap", snap.Value.Data)

[<Fact>]
let ``createAppendEntries returns None when leader has no LeaderState`` () =
    let state = State.init dummyConfig None
    let ae = Replication.createAppendEntries 2 state
    Assert.True ae.IsNone

[<Fact>]
let ``Session-based duplicate detection prevents duplicate command application`` () =
    let c1, c2, c3 = threeNodeConfigs ()
    let mutable s1 = State.init c1 None
    let mutable s2 = State.init c2 None
    let mutable s3 = State.init c3 None

    // 1. Node 1 becomes leader of Term 1
    let s1_new, s2_new, s3_new, _ = electLeader s1 s2 s3
    s1 <- s1_new
    s2 <- s2_new
    s3 <- s3_new
    Assert.Equal(Leader, s1.Role)

    // 2. Submit a command with session info
    s1 <- Replication.appendCommandWithSession "cmd1" "client-1" 1L s1
    Assert.Equal(2, s1.Persistent.Log.Count)

    // 3. Duplicate submission: same clientId and seqNum (simulates retry)
    s1 <- Replication.appendCommandWithSession "cmd1" "client-1" 1L s1
    // Log should NOT grow — duplicate entry is appended but that's expected at
    // the Replication level; dedup happens at the NodeAgent level via SessionTable.
    Assert.Equal(3, s1.Persistent.Log.Count)

    // 4. Replicate to followers
    let ae_to2 = (Replication.createAppendEntries 2 s1).Value
    let ae_to3 = (Replication.createAppendEntries 3 s1).Value
    let r_ae2, resp_ae2 = Replication.handleAppendEntries ae_to2 s2
    s2 <- r_ae2
    let r_ae3, resp_ae3 = Replication.handleAppendEntries ae_to3 s3
    s3 <- r_ae3
    s1 <- Replication.handleAppendEntriesResponse resp_ae2 s1
    s1 <- Replication.handleAppendEntriesResponse resp_ae3 s1
    s1 <- Replication.advanceCommitIndex s1
    Assert.Equal(3L, s1.Volatile.CommitIndex)

    // 5. Apply committed on leader (noop@1 skipped, cmd1@2 applied, cmd1@3 applied but skipped by session dedup)
    let applied = ResizeArray<string>()
    s1 <- NodeApply.applyCommitted (fun e -> applied.Add e.Command) s1
    // Only cmd1 should be applied once (index 3 is duplicate detected by SessionTable)
    Assert.Equal(3L, s1.Volatile.LastApplied)
    Assert.Single(applied) |> ignore
    Assert.Equal("cmd1", applied.[0])

[<Fact>]
let ``JointConsensus config change replicates and commits on followers`` () =
    let c1, c2, c3 = threeNodeConfigs ()
    let mutable s1 = State.init c1 None
    let mutable s2 = State.init c2 None
    let mutable s3 = State.init c3 None

    // 1. Node 1 becomes leader of Term 1
    let s1_new, s2_new, s3_new, _ = electLeader s1 s2 s3
    s1 <- s1_new
    s2 <- s2_new
    s3 <- s3_new
    Assert.Equal(Leader, s1.Role)

    // 2. Append JointConsensus entry on leader
    let oldPeers = c1.Peers
    let newPeers = [ { Id = 4; Host = ""; Port = 0 }; { Id = 5; Host = ""; Port = 0 } ]
    s1 <- Replication.appendJointConsensus oldPeers newPeers s1
    Assert.Equal(2, s1.Persistent.Log.Count)
    let configEntry = Map.find 2L s1.Persistent.Log
    Assert.StartsWith(ConfigChange.ConfigCommandPrefix, configEntry.Command)

    // 3. Replicate config entry to followers
    let ae_to2 = (Replication.createAppendEntries 2 s1).Value
    let ae_to3 = (Replication.createAppendEntries 3 s1).Value
    let r_ae2, resp_ae2 = Replication.handleAppendEntries ae_to2 s2
    s2 <- r_ae2
    let r_ae3, resp_ae3 = Replication.handleAppendEntries ae_to3 s3
    s3 <- r_ae3
    Assert.True resp_ae2.Success
    Assert.True resp_ae3.Success

    // 4. Leader processes responses and advances commit index
    s1 <- Replication.handleAppendEntriesResponse resp_ae2 s1
    s1 <- Replication.handleAppendEntriesResponse resp_ae3 s1
    s1 <- Replication.advanceCommitIndex s1
    Assert.Equal(2L, s1.Volatile.CommitIndex)

    // 5. Apply committed on leader - should transition to JointPhase
    s1 <- NodeApply.applyCommitted (fun _ -> ()) s1
    Assert.Equal(2L, s1.Volatile.LastApplied)

    match s1.ConfigPhase with
    | JointPhase(op, np) ->
        Assert.Equal(2, op.Length)
        Assert.Equal(2, np.Length)
    | _ -> Assert.Fail "Expected JointPhase"

    // 6. Followers need a heartbeat to advance commit index
    let hb_to2 = (Replication.createHeartbeat 2 s1).Value
    let hb_to3 = (Replication.createHeartbeat 3 s1).Value
    let r_hb2, _ = Replication.handleAppendEntries hb_to2 s2
    s2 <- r_hb2
    let r_hb3, _ = Replication.handleAppendEntries hb_to3 s3
    s3 <- r_hb3
    Assert.Equal(2L, s2.Volatile.CommitIndex)
    Assert.Equal(2L, s3.Volatile.CommitIndex)

    // 7. Apply committed on followers - they should also transition to JointPhase
    s2 <- NodeApply.applyCommitted (fun _ -> ()) s2

    match s2.ConfigPhase with
    | JointPhase _ -> ()
    | _ -> Assert.Fail "Expected JointPhase on follower"

[<Fact>]
let ``FinalConfiguration entry transitions from JointPhase to SinglePhase via applyCommitted`` () =
    // Use a 3-node cluster so quorum is achievable in both configs during JointPhase
    let c1, c2, c3 = threeNodeConfigs ()
    let mutable s1 = State.init c1 None
    let mutable s2 = State.init c2 None
    let mutable s3 = State.init c3 None

    // 1. Node 1 becomes leader of Term 1
    let s1_new, s2_new, s3_new, _ = electLeader s1 s2 s3
    s1 <- s1_new
    s2 <- s2_new
    s3 <- s3_new
    Assert.Equal(Leader, s1.Role)

    // 2. Manually enter JointPhase (simulating that JointChange was committed)
    let oldPeers = s1.Config.Peers
    // New config includes existing nodes so quorum can be reached
    let newPeers = [ { Id = 1; Host = ""; Port = 0 }; { Id = 2; Host = ""; Port = 0 } ]
    s1 <- State.enterJointConsensus oldPeers newPeers s1

    match s1.ConfigPhase with
    | JointPhase _ -> ()
    | _ -> Assert.Fail "Expected JointPhase"

    // 3. Append FinalConfiguration
    s1 <- Replication.appendFinalConfiguration newPeers s1
    Assert.Equal(2, s1.Persistent.Log.Count)
    let finalEntry = Map.find 2L s1.Persistent.Log
    Assert.StartsWith(ConfigChange.ConfigCommandPrefix, finalEntry.Command)
    Assert.True(finalEntry.Command.Contains("f"))

    // 4. Replicate to BOTH followers so quorum checks pass
    let ae_to2 = (Replication.createAppendEntries 2 s1).Value
    let r_ae2, resp_ae2 = Replication.handleAppendEntries ae_to2 s2
    s2 <- r_ae2
    s1 <- Replication.handleAppendEntriesResponse resp_ae2 s1
    let ae_to3 = (Replication.createAppendEntries 3 s1).Value
    let r_ae3, resp_ae3 = Replication.handleAppendEntries ae_to3 s3
    s3 <- r_ae3
    s1 <- Replication.handleAppendEntriesResponse resp_ae3 s1
    s1 <- Replication.advanceCommitIndex s1
    Assert.Equal(2L, s1.Volatile.CommitIndex)

    // 5. Apply committed on leader - should transition to SinglePhase
    s1 <- NodeApply.applyCommitted (fun _ -> ()) s1
    Assert.Equal(2L, s1.Volatile.LastApplied)
    Assert.Equal(SinglePhase, s1.ConfigPhase)
    Assert.Equal(2, s1.Config.Peers.Length)
    Assert.Contains(1, s1.Config.Peers |> List.map (fun p -> p.Id))
    Assert.Contains(2, s1.Config.Peers |> List.map (fun p -> p.Id))

[<Fact>]
let ``applyCommitted skips noop entries and only applies real commands`` () =
    let config1 =
        { NodeId = 1
          Host = ""
          Port = 0
          Peers = [ { Id = 2; Host = ""; Port = 0 } ]
          ElectionTimeoutMinMs = 1
          ElectionTimeoutMaxMs = 2
          HeartbeatIntervalMs = 1
          SnapshotAutoThreshold = 0 }

    let config2 =
        { config1 with
            NodeId = 2
            Peers = [ { Id = 1; Host = ""; Port = 0 } ] }

    let mutable s1 = State.init config1 None
    let mutable s2 = State.init config2 None

    // 1. Node 1 becomes leader (noop@1 is appended)
    s1 <- Election.startElection s1
    let rv1 = Election.createRequestVote s1
    let rv1_s2, resp2 = Election.handleRequestVote rv1 s2
    s2 <- rv1_s2
    s1 <- Election.handleVoteResponse 2 resp2 s1
    Assert.Equal(Leader, s1.Role)
    Assert.Equal(1, s1.Persistent.Log.Count)
    Assert.Equal(Log.NoOpCommand, (Map.find 1L s1.Persistent.Log).Command) // noop

    // 2. Append a real command
    s1 <- Replication.appendCommand "real-cmd" s1
    Assert.Equal(2, s1.Persistent.Log.Count)

    // 3. Replicate and commit
    let ae = (Replication.createAppendEntries 2 s1).Value
    let r_ae, resp_ae = Replication.handleAppendEntries ae s2
    s2 <- r_ae
    s1 <- Replication.handleAppendEntriesResponse resp_ae s1
    s1 <- Replication.advanceCommitIndex s1
    Assert.Equal(2L, s1.Volatile.CommitIndex)

    // 4. Apply committed — noop should be skipped, only real-cmd applied
    let applied = ResizeArray<string>()
    s1 <- NodeApply.applyCommitted (fun e -> applied.Add e.Command) s1
    Assert.Equal(2L, s1.Volatile.LastApplied)
    Assert.Single(applied) |> ignore
    Assert.Equal("real-cmd", applied.[0])
