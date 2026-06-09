namespace Raft

module Transport =
    let log msg = printfn "[Transport] %s" msg

    [<TailCall>]
    let rec readAsync
        (stream: System.Net.Sockets.NetworkStream)
        (ct: System.Threading.CancellationToken)
        buffer
        offset
        count
        acc
        =
        async {
            if count = 0 then
                return acc
            else
                let! bytesRead = stream.ReadAsync(buffer, offset, count, ct) |> Async.AwaitTask

                if bytesRead = 0 then
                    return acc
                else
                    return! readAsync stream ct buffer (offset + bytesRead) (count - bytesRead) (acc + bytesRead)
        }

    let readLengthPrefix (stream: System.Net.Sockets.NetworkStream) ct =
        async {
            let lenBuf = Array.zeroCreate 4
            let! lenRead = readAsync stream ct lenBuf 0 4 0

            if lenRead < 4 then
                return 0
            else
                return
                    int lenBuf.[0] <<< 24
                    ||| (int lenBuf.[1] <<< 16)
                    ||| (int lenBuf.[2] <<< 8)
                    ||| int lenBuf.[3]
        }

    let readAndDispatch stream ct postMessage =
        async {
            let! msgLen = readLengthPrefix stream ct

            if msgLen = 0 then
                return false
            else
                let msgBuf = Array.zeroCreate msgLen
                let! msgRead = readAsync stream ct msgBuf 0 msgLen 0

                if msgRead < msgLen then
                    return false
                else
                    let json = System.Text.Encoding.UTF8.GetString(msgBuf, 0, msgRead)

                    try
                        let msg =
                            System.Text.Json.JsonSerializer.Deserialize<RaftMessage>(json, JsonConfig.options)

                        postMessage msg
                    with ex ->
                        log $"Error deserializing message: {ex.Message}."

                    return true
        }

    let handleConnection
        (tcpClient: System.Net.Sockets.TcpClient)
        (ct: System.Threading.CancellationToken)
        postMessage
        =
        async {
            use client = tcpClient
            use stream = client.GetStream()

            try
                while not ct.IsCancellationRequested do
                    let! cont = readAndDispatch stream ct postMessage

                    if not cont then
                        ct.ThrowIfCancellationRequested()
                        ()
            with
            | :? System.ObjectDisposedException
            | :? System.OperationCanceledException -> ()
            | ex -> log $"Connection handler error: {ex.Message}."
        }

    let startListener config postMessage (ct: System.Threading.CancellationToken) =
        let listener =
            new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, config.Port)

        listener.Start()
        log $"Listening on port {config.Port}."

        async {
            use _reg =
                ct.Register(fun () ->
                    try
                        listener.Stop()
                    with _ ->
                        ())

            try
                while not ct.IsCancellationRequested do
                    let! tcpClient = listener.AcceptTcpClientAsync() |> Async.AwaitTask
                    handleConnection tcpClient ct postMessage |> Async.Start
            with
            | :? System.ObjectDisposedException -> ()
            | ex -> log $"Listener error: {ex.Message}."
        }

    let serializeMessage msg =
        System.Text.Json.JsonSerializer.Serialize(msg, JsonConfig.options)
        |> System.Text.Encoding.UTF8.GetBytes

    let buildFrame (bytes: byte[]) =
        let lenPrefix =
            [| byte (bytes.Length >>> 24)
               byte (bytes.Length >>> 16)
               byte (bytes.Length >>> 8)
               byte bytes.Length |]

        lenPrefix, bytes

    let sendMessage (peer: PeerInfo) msg =
        async {
            use cts = new System.Threading.CancellationTokenSource 3000

            try
                use client = new System.Net.Sockets.TcpClient()
                do! client.ConnectAsync(peer.Host, peer.Port, cts.Token).AsTask() |> Async.AwaitTask
                let lenPrefix, payload = serializeMessage msg |> buildFrame
                use stream = client.GetStream()
                do! stream.WriteAsync(lenPrefix, 0, lenPrefix.Length, cts.Token) |> Async.AwaitTask
                do! stream.WriteAsync(payload, 0, payload.Length, cts.Token) |> Async.AwaitTask
            with
            | :? System.OperationCanceledException -> log $"Timeout connecting to {peer.Id} ({peer.Host}:{peer.Port})."
            | ex -> log $"Failed to send to {peer.Id}: {ex.Message}."
        }

type TcpTransport() =
    interface ITransport with
        member _.StartListener config postMessage ct =
            Transport.startListener config postMessage ct

        member _.SendMessage peer msg = Transport.sendMessage peer msg
