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

    let lastIndexOfTerm term log =
        log
        |> List.tryFindBack (fun e -> e.Term = term)
        |> Option.map (fun e -> e.Index)

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
    let rec _merge logMap acc =
        function
        | [] -> acc
        | entry :: rest as remaining ->
            match logMap |> Map.tryFind entry.Index with
            | Some existing when existing.Term <> entry.Term -> truncateFrom entry.Index acc @ remaining
            | Some _ -> _merge logMap acc rest
            | None -> acc @ remaining

    let mergeEntries entries log =
        let logMap = log |> List.map (fun e -> e.Index, e) |> Map.ofList
        _merge logMap log entries
