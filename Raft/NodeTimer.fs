namespace Raft

module NodeTimer =
    let getRandomElectionTimeout config =
        System.Random.Shared.Next(config.ElectionTimeoutMinMs, config.ElectionTimeoutMaxMs)

    let createTimer (inbox: MailboxProcessor<NodeMessage>) msg interval =
        new System.Threading.Timer((fun _ -> inbox.Post msg), null, interval, System.Threading.Timeout.Infinite)

    let disposeTimer (timer: System.Threading.Timer option) =
        match timer with
        | Some t -> t.Dispose()
        | None -> ()

    let applyElectionAction ctx =
        function
        | Keep -> ctx.ElectionTimer
        | Reset ->
            let interval = getRandomElectionTimeout ctx.Config

            match ctx.ElectionTimer with
            | Some t ->
                t.Change(interval, System.Threading.Timeout.Infinite) |> ignore
                ctx.ElectionTimer
            | None -> Some(createTimer ctx.Inbox ElectionTimeout interval)
        | Stop ->
            match ctx.ElectionTimer with
            | Some t ->
                t.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
                |> ignore
            | None -> ()

            ctx.ElectionTimer

    let applyHeartbeatAction ctx =
        function
        | Keep -> ctx.HeartbeatTimer
        | Reset ->
            let interval = ctx.Config.HeartbeatIntervalMs

            match ctx.HeartbeatTimer with
            | Some t ->
                t.Change(interval, System.Threading.Timeout.Infinite) |> ignore
                ctx.HeartbeatTimer
            | None -> Some(createTimer ctx.Inbox HeartbeatTimeout interval)
        | Stop ->
            match ctx.HeartbeatTimer with
            | Some t ->
                t.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
                |> ignore
            | None -> ()

            ctx.HeartbeatTimer

    let getTimerActionsOnRoleChange ctx oldRole newState sendReply =
        if oldRole <> Leader && newState.Role = Leader then
            NodeBroadcaster.broadcastHeartbeat ctx.Config ctx.Transport newState
            Stop, Reset
        elif oldRole = Leader && newState.Role <> Leader then
            Reset, Stop
        elif sendReply then
            Reset, Keep
        else
            Keep, Keep
