namespace Raft

module NodeUtil =
    let log msg = printfn "[Node] %s" msg

    let sendAsync (transport: ITransport) peer msg =
        let task =
            try
                transport.SendMessage peer msg
            with ex ->
                System.Threading.Tasks.Task.FromException<unit> ex

        task.ContinueWith(fun (t: System.Threading.Tasks.Task) ->
            if t.IsFaulted then
                let exMsg =
                    match t.Exception with
                    | null -> "(unknown)"
                    | ae -> ae.GetBaseException().Message

                log $"Failed to send to {peer.Id}: {exMsg}")
        |> ignore

    let saveIfChanged ctx state =
        if ctx.State.Persistent <> state.Persistent then
            ctx.Persistence.Save state.Persistent
