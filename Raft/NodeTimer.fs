namespace Raft

module NodeTimer =
    let getRandomElectionTimeout config =
        System.Random.Shared.Next(config.ElectionTimeoutMinMs, config.ElectionTimeoutMaxMs)

    let createTimer (inbox: MailboxProcessor<NodeMessage>) msg interval =
        new System.Threading.Timer((fun _ -> inbox.Post msg), null, interval, System.Threading.Timeout.Infinite)

    let changeTimer (timer: System.Threading.Timer) interval =
        timer.Change(interval, System.Threading.Timeout.Infinite) |> ignore
        timer

    let resetTimer (inbox: MailboxProcessor<NodeMessage>) (timer: System.Threading.Timer option) msg interval =
        match timer with
        | Some t -> Some(changeTimer t interval)
        | None -> Some(createTimer inbox msg interval)

    let stopTimer (timer: System.Threading.Timer option) =
        match timer with
        | Some t ->
            t.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite)
            |> ignore
        | None -> ()

    let disposeTimer (timer: System.Threading.Timer option) =
        match timer with
        | Some t -> t.Dispose()
        | None -> ()

    let resetElectionTimer ctx =
        resetTimer ctx.Inbox ctx.ElectionTimer ElectionTimeout (getRandomElectionTimeout ctx.Config)

    let resetHeartbeatTimer ctx =
        resetTimer ctx.Inbox ctx.HeartbeatTimer HeartbeatTimeout ctx.Config.HeartbeatIntervalMs
