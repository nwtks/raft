module Raft.Tests.TransportTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Raft

let dummyConfig port =
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

[<Fact>]
let ``sendMessage triggers listener and receives message`` () =
    let port = 15001
    let config = dummyConfig port
    let cts = new CancellationTokenSource()
    let tcs = TaskCompletionSource<RaftMessage>()
    let postMessage msg = tcs.TrySetResult msg |> ignore
    let tcpTransport = TcpTransport() :> ITransport
    tcpTransport.StartListener config postMessage cts.Token |> ignore

    Thread.Sleep 500

    let reqVote =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let msg = RequestVoteMsg reqVote
    tcpTransport.SendMessage (dummyPeer port) msg
    let received = tcs.Task.Wait(TimeSpan.FromSeconds 5.0)
    cts.Cancel()
    Assert.True(received, "Message was not received within timeout")

    let receivedMsg = tcs.Task.Result

    match receivedMsg with
    | RequestVoteMsg rv ->
        Assert.Equal(1L, rv.CandidateTerm)
        Assert.Equal(2, rv.CandidateId)
    | _ -> Assert.Fail "Received incorrect message type"
