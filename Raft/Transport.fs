namespace Raft

module Transport =
    let jsonOptions =
        let options = System.Text.Json.JsonSerializerOptions()
        options.Converters.Add(OptionConverterFactory())
        options.Converters.Add(RaftMessageConverter())
        options

    let log msg = printfn "[Transport] %s" msg

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
                        let buffer = Array.zeroCreate 65536
                        let! bytesRead = stream.ReadAsync(buffer, 0, buffer.Length, ct) |> Async.AwaitTask

                        if bytesRead > 0 then
                            let json = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead)

                            try
                                let msg =
                                    System.Text.Json.JsonSerializer.Deserialize<RaftMessage>(json, jsonOptions)

                                postMessage msg
                            with ex ->
                                log $"Error deserializing message: {ex.Message}."
                    }
                    |> Async.Start
            with
            | :? System.ObjectDisposedException -> ()
            | ex -> log $"Listener error: {ex.Message}."
        }

    let sendMessage (peer: PeerInfo) msg =
        task {
            try
                use client = new System.Net.Sockets.TcpClient()
                let connectTask = client.ConnectAsync(peer.Host, peer.Port)
                let timeoutTask = System.Threading.Tasks.Task.Delay 3000
                let! completed = System.Threading.Tasks.Task.WhenAny(connectTask, timeoutTask)

                if completed = connectTask && client.Connected then
                    let bytes =
                        System.Text.Json.JsonSerializer.Serialize(msg, jsonOptions)
                        |> System.Text.Encoding.UTF8.GetBytes

                    use stream = client.GetStream()
                    do! stream.WriteAsync(bytes, 0, bytes.Length)
                else
                    log $"Timeout connecting to {peer.Id} ({peer.Host}:{peer.Port})."
            with ex ->
                log $"Failed to send to {peer.Id}: {ex.Message}."
        }
        |> ignore

type TcpTransport() =
    interface ITransport with
        member _.StartListener config postMessage ct =
            Transport.startListener config postMessage ct

        member _.SendMessage peer msg = Transport.sendMessage peer msg
