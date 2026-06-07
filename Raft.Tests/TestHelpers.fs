module Raft.Tests.TestHelpers

open Raft

type MockTransport() =
    let messages = ResizeArray<PeerInfo * RaftMessage>()
    let mutable postMessageOpt: (RaftMessage -> unit) option = None
    member _.Messages = messages

    member _.ReceiveMessage msg =
        match postMessageOpt with
        | Some cb -> cb msg
        | None -> ()

    interface ITransport with
        member _.SendMessage peer msg =
            messages.Add((peer, msg))
            System.Threading.Tasks.Task.FromResult(())

        member _.StartListener _ postMessage _ =
            postMessageOpt <- Some postMessage
            System.Threading.Tasks.Task.FromResult(())

type MockPersistence() =
    let mutable stateOpt: PersistentState option = None

    interface IPersistence with
        member _.Save state = stateOpt <- Some state
        member _.Load() = stateOpt

let dummyConfig =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = 5001
      Peers =
        [ { Id = 2
            Host = "127.0.0.1"
            Port = 5002 }
          { Id = 3
            Host = "127.0.0.1"
            Port = 5003 } ]
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500
      SnapshotAutoThreshold = 0 }

let dummyConfigStandalone =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = 15001
      Peers = []
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500
      SnapshotAutoThreshold = 0 }

let dummyConfigWithPort port =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = port
      Peers = []
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500
      SnapshotAutoThreshold = 0 }

let configForNode id port =
    { NodeId = id
      Host = "127.0.0.1"
      Port = port
      Peers = []
      ElectionTimeoutMinMs = 100
      ElectionTimeoutMaxMs = 200
      HeartbeatIntervalMs = 50
      SnapshotAutoThreshold = 0 }

let configWithPeers id port =
    { NodeId = id
      Host = "127.0.0.1"
      Port = port
      Peers =
        [ { Id = 2
            Host = "127.0.0.1"
            Port = 16002 }
          { Id = 3
            Host = "127.0.0.1"
            Port = 16003 } ]
      ElectionTimeoutMinMs = 100
      ElectionTimeoutMaxMs = 200
      HeartbeatIntervalMs = 50
      SnapshotAutoThreshold = 0 }

let dummyPeer port =
    { Id = 2
      Host = "127.0.0.1"
      Port = port }

let logFromList (entries: LogEntry list) =
    entries |> List.map (fun e -> e.Index, e) |> Map.ofList
