namespace Raft

module Log =
    let empty: LogEntry list = []

    let lastIndex =
        function
        | [] -> 0L
        | log -> (List.last log).Index

    let lastTerm =
        function
        | [] -> 0L
        | log -> (List.last log).Term

    let getEntry index log =
        log |> List.tryFind (fun e -> e.Index = index)

    let termAt index log =
        match getEntry index log with
        | Some entry -> entry.Term
        | None -> 0L

    let appendEntry entry log = log @ [ entry ]

    let append term command log =
        let nextIndex = lastIndex log + 1L

        let entry =
            { Index = nextIndex
              Term = term
              Command = command }

        appendEntry entry log

    let truncateFrom index log =
        log |> List.takeWhile (fun e -> e.Index < index)

    let entriesFrom index log =
        log |> List.skipWhile (fun e -> e.Index < index)

    [<TailCall>]
    let rec _merge acc =
        function
        | [] -> acc
        | entry :: rest as remaining ->
            match getEntry entry.Index acc with
            | Some existing when existing.Term <> entry.Term ->
                let truncated = truncateFrom entry.Index acc
                truncated @ remaining
            | Some _ -> _merge acc rest
            | None -> acc @ remaining

    let mergeEntries entries log = _merge log entries
