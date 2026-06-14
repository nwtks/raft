module Raft.Tests.NodeBroadcasterTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``NodeBroadcaster.broadcastHeartbeat sends nothing when not leader`` () =
    let transport = MockTransport()
    let state = State.init dummyConfig None
    NodeBroadcaster.broadcastHeartbeat dummyConfig transport state
    Assert.Empty transport.Messages

[<Fact>]
let ``NodeBroadcaster.sendAppendEntriesOrSnapshot sends InstallSnapshot when follower is behind snapshot`` () =
    let transport = MockTransport()

    let state: RaftState =
        { Role = Leader
          Persistent =
            { CurrentTerm = 1L
              VotedFor = None
              Log =
                Map.ofList
                    [ (1L,
                       { Index = 1L
                         Term = 1L
                         Command = Log.NoOpCommand
                         ClientId = None
                         SeqNum = None }) ]
              Snapshot =
                Some
                    { LastIncludedIndex = 1L
                      LastIncludedTerm = 1L
                      StateMachineData = "snap-data" }
              SessionTable = Map.empty
              LastConfigIndex = 0L }
          Volatile = { CommitIndex = 1L; LastApplied = 1L }
          LeaderState =
            Some
                { NextIndex = Map.ofList [ (2, 1L) ]
                  MatchIndex = Map.ofList [ (2, 0L) ] }
          VotesReceived = Set.empty
          CurrentLeader = Some 1
          Config =
            { NodeId = 1
              Host = "127.0.0.1"
              Port = 0
              Peers = [ { Id = 2; Host = "127.0.0.1"; Port = 0 } ]
              ElectionTimeoutMinMs = 100
              ElectionTimeoutMaxMs = 200
              HeartbeatIntervalMs = 50
              SnapshotAutoThreshold = 0 }
          ConfigPhase = SinglePhase
          NonVotingPeers = [] }

    let peer: PeerInfo = { Id = 2; Host = "127.0.0.1"; Port = 0 }
    NodeBroadcaster.sendAppendEntriesOrSnapshot transport peer state

    Assert.Contains(
        transport.Messages,
        fun (p, msg) ->
            p.Id = 2
            && match msg with
               | InstallSnapshotMsg snap -> snap.Data = "snap-data" && snap.LastIncludedIndex = 1L
               | _ -> false
    )

[<Fact>]
let ``NodeBroadcaster.sendAppendEntriesOrSnapshot does nothing when neither AppendEntries nor InstallSnapshot can be created``
    ()
    =
    let transport = MockTransport()
    let state = State.init dummyConfig None
    let peer: PeerInfo = { Id = 2; Host = "127.0.0.1"; Port = 0 }
    NodeBroadcaster.sendAppendEntriesOrSnapshot transport peer state
    Assert.Empty transport.Messages
