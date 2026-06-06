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

    let startListener config postMessage (ct: System.Threading.CancellationToken) =
        task {
            let listener =
                new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, config.Port)

            listener.Start()
            log $"Listening on port {config.Port}."

            use _reg =
                ct.Register(fun () ->
                    try
                        listener.Stop()
                    with _ ->
                        ())

            try
                while not ct.IsCancellationRequested do
                    let! client = listener.AcceptTcpClientAsync()

                    async {
                        use client = client
                        use stream = client.GetStream()

                        try
                            while not ct.IsCancellationRequested do
                                let lenBuf = Array.zeroCreate 4
                                let! lenRead = readAsync stream ct lenBuf 0 4 0

                                if lenRead < 4 then
                                    ct.ThrowIfCancellationRequested()
                                    ()
                                else
                                    let msgLen =
                                        int lenBuf.[0] <<< 24
                                        ||| (int lenBuf.[1] <<< 16)
                                        ||| (int lenBuf.[2] <<< 8)
                                        ||| int lenBuf.[3]

                                    let msgBuf = Array.zeroCreate msgLen
                                    let! msgRead = readAsync stream ct msgBuf 0 msgLen 0

                                    if msgRead < msgLen then
                                        ct.ThrowIfCancellationRequested()
                                        ()
                                    else
                                        let json = System.Text.Encoding.UTF8.GetString(msgBuf, 0, msgRead)

                                        try
                                            let msg =
                                                System.Text.Json.JsonSerializer.Deserialize<RaftMessage>(
                                                    json,
                                                    JsonConfig.options
                                                )

                                            postMessage msg
                                        with ex ->
                                            log $"Error deserializing message: {ex.Message}."
                        with
                        | :? System.ObjectDisposedException
                        | :? System.OperationCanceledException -> ()
                        | ex -> log $"Connection handler error: {ex.Message}."
                    }
                    |> Async.Start
            with
            | :? System.ObjectDisposedException -> ()
            | ex -> log $"Listener error: {ex.Message}."
        }

    let sendMessage (peer: PeerInfo) msg =
        task {
            use cts = new System.Threading.CancellationTokenSource 3000

            try
                use client = new System.Net.Sockets.TcpClient()
                do! client.ConnectAsync(peer.Host, peer.Port, cts.Token)

                let bytes =
                    System.Text.Json.JsonSerializer.Serialize(msg, JsonConfig.options)
                    |> System.Text.Encoding.UTF8.GetBytes

                let msgLen = bytes.Length

                let lenPrefix =
                    [| byte (msgLen >>> 24)
                       byte (msgLen >>> 16)
                       byte (msgLen >>> 8)
                       byte msgLen |]

                use stream = client.GetStream()
                do! stream.WriteAsync(lenPrefix, 0, lenPrefix.Length, cts.Token)
                do! stream.WriteAsync(bytes, 0, bytes.Length, cts.Token)
            with
            | :? System.OperationCanceledException -> log $"Timeout connecting to {peer.Id} ({peer.Host}:{peer.Port})."
            | ex -> log $"Failed to send to {peer.Id}: {ex.Message}."
        }

type TcpTransport() =
    interface ITransport with
        member _.StartListener config postMessage ct =
            Transport.startListener config postMessage ct

        member _.SendMessage peer msg = Transport.sendMessage peer msg
