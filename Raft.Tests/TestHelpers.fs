module Raft.Tests.TestHelpers

open Raft

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
      HeartbeatIntervalMs = 500 }

let dummyConfigStandalone =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = 15001
      Peers = []
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500 }

let dummyConfigWithPort port =
    { NodeId = 1
      Host = "127.0.0.1"
      Port = port
      Peers = []
      ElectionTimeoutMinMs = 1500
      ElectionTimeoutMaxMs = 3000
      HeartbeatIntervalMs = 500 }

let dummyPeer port =
    { Id = 2
      Host = "127.0.0.1"
      Port = port }

let logFromList (entries: LogEntry list) =
    entries |> List.map (fun e -> e.Index, e) |> Map.ofList
