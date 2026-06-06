namespace Raft

module Log =
    let empty: Map<LogIndex, LogEntry> = Map.empty

    let lastIndex log =
        if Map.isEmpty log then 0L else log |> Map.keys |> Seq.max

    let lastTerm log =
        let idx = lastIndex log
        if idx = 0L then 0L else log.[idx].Term

    let getEntry index log = log |> Map.tryFind index

    let termAt index log =
        match getEntry index log with
        | Some entry -> entry.Term
        | None -> 0L

    let lastIndexOfTerm term log =
        log
        |> Map.toSeq
        |> Seq.tryFindBack (fun (_, e) -> e.Term = term)
        |> Option.map fst

    let appendEntry entry log = log |> Map.add entry.Index entry

    let append term command log =
        let nextIndex = lastIndex log + 1L

        let entry =
            { Index = nextIndex
              Term = term
              Command = command }

        appendEntry entry log

    let entriesFrom index log =
        log |> Map.toList |> List.skipWhile (fun (k, _) -> k < index) |> List.map snd

    [<TailCall>]
    let rec _merge log =
        function
        | [] -> log
        | entry :: rest ->
            match log |> Map.tryFind entry.Index with
            | Some existing when existing.Term <> entry.Term ->
                let before = log |> Map.filter (fun k _ -> k < entry.Index)
                entry :: rest |> List.fold (fun m e -> Map.add e.Index e m) before
            | Some _ -> _merge log rest
            | None -> entry :: rest |> List.fold (fun m e -> Map.add e.Index e m) log

    let mergeEntries entries log = _merge log entries
