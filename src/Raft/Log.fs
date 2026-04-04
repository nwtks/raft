namespace Raft

module Log =
    let empty: LogEntry list = []

    let lastIndex (log: LogEntry list) : LogIndex =
        match log with
        | [] -> 0L
        | _ -> (List.last log).Index

    let lastTerm (log: LogEntry list) : Term =
        match log with
        | [] -> 0L
        | _ -> (List.last log).Term

    let getEntry (index: LogIndex) (log: LogEntry list) =
        log |> List.tryFind (fun e -> e.Index = index)

    let termAt (index: LogIndex) (log: LogEntry list) : Term =
        match getEntry index log with
        | Some entry -> entry.Term
        | None -> 0L

    let appendEntry (entry: LogEntry) (log: LogEntry list) = log @ [ entry ]

    let append (term: Term) (command: string) (log: LogEntry list) =
        let nextIndex = lastIndex log + 1L

        let entry =
            { Index = nextIndex
              Term = term
              Command = command }

        appendEntry entry log

    let truncateFrom (index: LogIndex) (log: LogEntry list) =
        log |> List.takeWhile (fun e -> e.Index < index)

    let entriesFrom (index: LogIndex) (log: LogEntry list) =
        log |> List.skipWhile (fun e -> e.Index < index)

    [<TailCall>]
    let rec _merge (acc: LogEntry list) (remaining: LogEntry list) =
        match remaining with
        | [] -> acc
        | entry :: rest ->
            match getEntry entry.Index acc with
            | Some existing when existing.Term <> entry.Term ->
                let truncated = truncateFrom entry.Index acc
                truncated @ remaining
            | Some _ -> _merge acc rest
            | None -> acc @ remaining

    let mergeEntries (entries: LogEntry list) (log: LogEntry list) = _merge log entries
