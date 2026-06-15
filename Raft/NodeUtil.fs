namespace Raft

module NodeUtil =
    let log msg = printfn "[Node] %s" msg

    let sendAsync (transport: ITransport) peer msg =
        let sendOp =
            try
                transport.SendMessage peer msg
            with ex ->
                log $"Failed to send to {peer.Id}: {ex.Message}"
                async { () }

        async {
            try
                do! sendOp
            with ex ->
                log $"Failed to send to {peer.Id}: {ex.Message}"
        }
        |> Async.Start

    let saveIfChanged ctx state =
        if ctx.State.Persistent <> state.Persistent then
            ctx.Persistence.Save state.Persistent
