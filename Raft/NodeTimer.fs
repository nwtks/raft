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

    let applyTimerAction (getTimer: NodeContext -> System.Threading.Timer option) getInterval createMsg ctx action =
        match action with
        | Keep -> getTimer ctx
        | Reset ->
            let interval = getInterval ctx

            match getTimer ctx with
            | Some t ->
                t.Change(interval, System.Threading.Timeout.Infinite) |> ignore
                getTimer ctx
            | None -> Some(createTimer ctx.Inbox createMsg interval)
        | Stop ->
            match getTimer ctx with
            | Some t ->
                t.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
                |> ignore
            | None -> ()

            getTimer ctx

    let applyElectionAction ctx =
        applyTimerAction
            (fun ctx -> ctx.ElectionTimer)
            (fun ctx -> getRandomElectionTimeout ctx.Config)
            ElectionTimeout
            ctx

    let applyHeartbeatAction ctx =
        applyTimerAction
            (fun ctx -> ctx.HeartbeatTimer)
            (fun ctx -> ctx.Config.HeartbeatIntervalMs)
            HeartbeatTimeout
            ctx

    let getTimerActionsOnRoleChange oldRole newState sendReply =
        let wasLeader = oldRole = Leader
        let isLeader = newState.Role = Leader

        match wasLeader, isLeader with
        | false, true -> Stop, Reset
        | true, false -> Reset, Stop
        | _ when sendReply -> Reset, Keep
        | _ -> Keep, Keep
