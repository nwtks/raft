namespace Raft

type PersistentState =
    { CurrentTerm: Term
      VotedFor: NodeId option
      Log: Map<LogIndex, LogEntry> }

type IPersistence =
    abstract member Save: PersistentState -> unit
    abstract member Load: unit -> PersistentState option

type VolatileState =
    { CommitIndex: LogIndex
      LastApplied: LogIndex }

type LeaderState =
    { NextIndex: Map<NodeId, LogIndex>
      MatchIndex: Map<NodeId, LogIndex> }

type RaftState =
    { Role: NodeRole
      Persistent: PersistentState
      Volatile: VolatileState
      LeaderState: LeaderState option
      VotesReceived: Set<NodeId>
      CurrentLeader: NodeId option
      Config: NodeConfig }

module State =
    let init config loadedState =
        let persistent =
            match loadedState with
            | Some p -> p
            | None ->
                { CurrentTerm = 0L
                  VotedFor = None
                  Log = Log.empty }

        { Role = Follower
          Persistent = persistent
          Volatile = { CommitIndex = 0L; LastApplied = 0L }
          LeaderState = None
          VotesReceived = Set.empty
          CurrentLeader = None
          Config = config }

    let initLeaderState state =
        let nextIdx = Log.lastIndex state.Persistent.Log + 1L

        let leaderState =
            { NextIndex = state.Config.Peers |> List.map (fun p -> p.Id, nextIdx) |> Map.ofList
              MatchIndex = state.Config.Peers |> List.map (fun p -> p.Id, 0L) |> Map.ofList }

        { state with
            Role = Leader
            LeaderState = Some leaderState
            CurrentLeader = Some state.Config.NodeId }

    let updateTerm newTerm state =
        if newTerm > state.Persistent.CurrentTerm then
            { state with
                Persistent =
                    { state.Persistent with
                        CurrentTerm = newTerm
                        VotedFor = None }
                Role = Follower
                LeaderState = None
                VotesReceived = Set.empty }
        else
            state

    let updateLog newLog state =
        { state with
            Persistent = { state.Persistent with Log = newLog } }

    let updateLogAndCommit newLog newCommitIndex state =
        { state with
            Persistent = { state.Persistent with Log = newLog }
            Volatile =
                { state.Volatile with
                    CommitIndex = newCommitIndex } }

    let updateCommitIndex newCommitIndex state =
        { state with
            Volatile =
                { state.Volatile with
                    CommitIndex = newCommitIndex } }

    let updateLastApplied newLastApplied state =
        { state with
            Volatile =
                { state.Volatile with
                    LastApplied = newLastApplied } }

    let followLeader leaderTerm leaderId state =
        let s = updateTerm leaderTerm state

        { s with
            Role = Follower
            CurrentLeader = Some leaderId }

    let updateLeaderState newNextIndex newMatchIndex state =
        match state.LeaderState with
        | Some ls ->
            { state with
                LeaderState =
                    Some
                        { ls with
                            NextIndex = newNextIndex
                            MatchIndex = newMatchIndex } }
        | None -> state

    let recordVote candidateId state =
        { state with
            Persistent =
                { state.Persistent with
                    VotedFor = Some candidateId } }

    let addVoteReceived nodeId state =
        { state with
            VotesReceived = state.VotesReceived |> Set.add nodeId }

    let quorumSize state =
        (List.length state.Config.Peers + 1) / 2 + 1
