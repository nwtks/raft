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

    let entriesFrom index log =
        log |> Map.toList |> List.skipWhile (fun (k, _) -> k < index) |> List.map snd

    let createEntry term command clientId seqNum log =
        let nextIndex = lastIndex log + 1L

        { Index = nextIndex
          Term = term
          Command = command
          ClientId = clientId
          SeqNum = seqNum }

    let append term command log =
        let entry = createEntry term command None None log
        log |> Map.add entry.Index entry

    let appendWithSession term command clientId seqNum log =
        let entry = createEntry term command (Some clientId) (Some seqNum) log
        log |> Map.add entry.Index entry

    let appendEntriesToLog log entries =
        entries |> List.fold (fun m e -> Map.add e.Index e m) log

    let truncateAndAppend log entry rest =
        let before =
            log |> Map.toSeq |> Seq.takeWhile (fun (k, _) -> k < entry.Index) |> Map.ofSeq

        entry :: rest |> appendEntriesToLog before

    [<TailCall>]
    let rec _merge log =
        function
        | [] -> log
        | entry :: rest ->
            match log |> Map.tryFind entry.Index with
            | Some existing when existing.Term <> entry.Term -> truncateAndAppend log entry rest
            | Some _ -> _merge log rest
            | None -> entry :: rest |> appendEntriesToLog log

    let mergeEntries entries log = _merge log entries

    let trim lastIncludedIndex lastIncludedTerm log =
        let sentinel =
            { Index = lastIncludedIndex
              Term = lastIncludedTerm
              Command = ""
              ClientId = None
              SeqNum = None }

        log
        |> Map.filter (fun k _ -> k > lastIncludedIndex)
        |> Map.add lastIncludedIndex sentinel
