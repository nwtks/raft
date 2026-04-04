namespace Raft

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks

module Transport =
    let jsonOptions =
        let options = JsonSerializerOptions()
        options.Converters.Add(JsonFSharpConverter())
        options

    let log (msg: string) = printfn "[Transport] %s" msg

    let startListener (config: NodeConfig) (postMessage: RaftMessage -> unit) (ct: CancellationToken) =
        task {
            let listener = new TcpListener(IPAddress.Any, config.Port)
            listener.Start()
            log $"Listening on port {config.Port}"

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
                            let json = Encoding.UTF8.GetString(buffer, 0, bytesRead)

                            try
                                let msg = JsonSerializer.Deserialize<RaftMessage>(json, jsonOptions)
                                postMessage msg
                            with ex ->
                                log $"Error deserializing message: {ex.Message}"
                    }
                    |> Async.Start
            with
            | :? ObjectDisposedException -> ()
            | ex -> log $"Listener error: {ex.Message}"
        }

    let sendMessage (peer: PeerInfo) (msg: RaftMessage) =
        task {
            try
                use client = new TcpClient()
                let connectTask = client.ConnectAsync(peer.Host, peer.Port)
                let timeoutTask = Task.Delay 3000
                let! completed = Task.WhenAny(connectTask, timeoutTask)

                if completed = connectTask && client.Connected then
                    let bytes = JsonSerializer.Serialize(msg, jsonOptions) |> Encoding.UTF8.GetBytes
                    use stream = client.GetStream()
                    do! stream.WriteAsync(bytes, 0, bytes.Length)
                else
                    log $"Timeout connecting to {peer.Id} ({peer.Host}:{peer.Port})"
            with ex ->
                log $"Failed to send to {peer.Id}: {ex.Message}"
        }
        |> ignore

type TcpTransport() =
    interface ITransport with
        member _.StartListener (config: NodeConfig) (postMessage: RaftMessage -> unit) (ct: CancellationToken) =
            Transport.startListener config postMessage ct

        member _.SendMessage (peer: PeerInfo) (msg: RaftMessage) = Transport.sendMessage peer msg
