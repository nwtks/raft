module Raft.Tests.TransportTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``TcpTransport.SendMessage triggers listener callback with message on loopback`` () =
    let port = 15001
    let config = dummyConfigWithPort port
    let cts = new System.Threading.CancellationTokenSource()
    let tcs = System.Threading.Tasks.TaskCompletionSource<RaftMessage>()
    let postMessage msg = tcs.TrySetResult msg |> ignore
    let tcpTransport = TcpTransport() :> ITransport
    tcpTransport.StartListener config postMessage cts.Token |> Async.Start
    System.Threading.Thread.Sleep 500

    let reqVote =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let msg = RequestVoteMsg reqVote
    tcpTransport.SendMessage (dummyPeer port) msg |> Async.RunSynchronously
    let received = tcs.Task.Wait(System.TimeSpan.FromSeconds 5.0)
    cts.Cancel()
    Assert.True(received, "Message was not received within timeout")

    let receivedMsg = tcs.Task.Result

    match receivedMsg with
    | RequestVoteMsg rv ->
        Assert.Equal(1L, rv.CandidateTerm)
        Assert.Equal(2, rv.CandidateId)
    | _ -> Assert.Fail "Received incorrect message type"
