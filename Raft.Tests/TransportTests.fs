module Raft.Tests.TransportTests

open Xunit
open Raft
open TestHelpers

let waitForPort port (timeoutMs: int) =
    System.Threading.SpinWait.SpinUntil(
        (fun () ->
            try
                use client = new System.Net.Sockets.TcpClient()
                client.Connect("127.0.0.1", port)
                true
            with _ ->
                false),
        timeoutMs
    )
    |> ignore

[<Fact>]
let ``TcpTransport.SendMessage triggers listener callback with message on loopback`` () =
    let config = dummyConfigWithPort 0
    let cts = new System.Threading.CancellationTokenSource()
    let tcs = System.Threading.Tasks.TaskCompletionSource<RaftMessage>()
    let postMessage msg = tcs.TrySetResult msg |> ignore
    let tcpTransport = TcpTransport()

    (tcpTransport :> ITransport).StartListener config postMessage cts.Token
    |> Async.Start

    waitForPort tcpTransport.BoundPort 5000

    let reqVote =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    let msg = RequestVoteMsg reqVote

    (tcpTransport :> ITransport).SendMessage (dummyPeer tcpTransport.BoundPort) msg
    |> Async.RunSynchronously

    let received = tcs.Task.Wait(System.TimeSpan.FromSeconds 5.0)
    cts.Cancel()
    Assert.True(received, "Message was not received within timeout")

    let receivedMsg = tcs.Task.Result

    match receivedMsg with
    | RequestVoteMsg rv ->
        Assert.Equal(1L, rv.CandidateTerm)
        Assert.Equal(2, rv.CandidateId)
    | _ -> Assert.Fail "Received incorrect message type"

[<Fact>]
let ``TcpTransport.SendMessage handles connection refused without throwing`` () =
    let tcpTransport = TcpTransport() :> ITransport

    let unreachablePeer =
        { Id = 99
          Host = "127.0.0.1"
          Port = 29999 }

    let msg =
        RequestVoteMsg
            { CandidateTerm = 1L
              CandidateId = 2
              LastLogIndex = 0L
              LastLogTerm = 0L }

    tcpTransport.SendMessage unreachablePeer msg |> Async.RunSynchronously

[<Fact>]
let ``TcpTransport listener handles deserialization error without crashing`` () =
    let config = dummyConfigWithPort 0
    let cts = new System.Threading.CancellationTokenSource()
    let tcs = System.Threading.Tasks.TaskCompletionSource<RaftMessage>()
    let postMessage msg = tcs.TrySetResult msg |> ignore
    let tcpTransport = TcpTransport()

    (tcpTransport :> ITransport).StartListener config postMessage cts.Token
    |> Async.Start

    waitForPort tcpTransport.BoundPort 5000

    use client = new System.Net.Sockets.TcpClient()
    client.Connect("127.0.0.1", tcpTransport.BoundPort)
    use stream = client.GetStream()
    let garbage = System.Text.Encoding.UTF8.GetBytes("not valid json")
    let len = garbage.Length

    let lenPrefix =
        [| byte (len >>> 24); byte (len >>> 16); byte (len >>> 8); byte len |]

    stream.Write(lenPrefix, 0, 4)
    stream.Write(garbage, 0, garbage.Length)

    let reqVote =
        { CandidateTerm = 1L
          CandidateId = 2
          LastLogIndex = 0L
          LastLogTerm = 0L }

    (tcpTransport :> ITransport).SendMessage (dummyPeer tcpTransport.BoundPort) (RequestVoteMsg reqVote)
    |> Async.RunSynchronously

    let received = tcs.Task.Wait(System.TimeSpan.FromSeconds 5.0)
    cts.Cancel()
    Assert.True(received, "Listener should still process valid messages after deserialization error")

    match tcs.Task.Result with
    | RequestVoteMsg rv -> Assert.Equal(1L, rv.CandidateTerm)
    | _ -> Assert.Fail "Wrong message type"

[<Fact>]
let ``TcpTransport handles zero-length frame and disconnect`` () =
    let config = dummyConfigWithPort 0
    let cts = new System.Threading.CancellationTokenSource()
    let postMessage _ = ()
    let tcpTransport = TcpTransport()

    (tcpTransport :> ITransport).StartListener config postMessage cts.Token
    |> Async.Start

    waitForPort tcpTransport.BoundPort 5000

    use client = new System.Net.Sockets.TcpClient()
    client.Connect("127.0.0.1", tcpTransport.BoundPort)
    let stream = client.GetStream()
    let lenPrefix = [| 0uy; 0uy; 0uy; 0uy |]
    stream.Write(lenPrefix, 0, 4)
    client.Close()
    System.Threading.Thread.Sleep 200
    cts.Cancel()

[<Fact>]
let ``TcpTransport handles partial message frame`` () =
    let config = dummyConfigWithPort 0
    let cts = new System.Threading.CancellationTokenSource()
    let postMessage _ = ()
    let tcpTransport = TcpTransport()

    (tcpTransport :> ITransport).StartListener config postMessage cts.Token
    |> Async.Start

    waitForPort tcpTransport.BoundPort 5000

    use client = new System.Net.Sockets.TcpClient()
    client.Connect("127.0.0.1", tcpTransport.BoundPort)
    let stream = client.GetStream()
    let largeLen = 100000

    let lenPrefix =
        [| byte (largeLen >>> 24)
           byte (largeLen >>> 16)
           byte (largeLen >>> 8)
           byte largeLen |]

    stream.Write(lenPrefix, 0, 4)
    let partialData = Array.zeroCreate 5
    stream.Write(partialData, 0, 5)
    client.Close()
    System.Threading.Thread.Sleep 200
    cts.Cancel()
