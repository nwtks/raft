namespace Raft

type Snapshot =
    { LastIncludedIndex: LogIndex
      LastIncludedTerm: Term
      StateMachineData: string }

type PersistentState =
    { CurrentTerm: Term
      VotedFor: NodeId option
      Log: Map<LogIndex, LogEntry>
      Snapshot: Snapshot option
      SessionTable: Map<string, int64> }

type VolatileState =
    { CommitIndex: LogIndex
      LastApplied: LogIndex }

type LeaderState =
    { NextIndex: Map<NodeId, LogIndex>
      MatchIndex: Map<NodeId, LogIndex> }

type ConfigPhase =
    | SinglePhase
    | JointPhase of oldPeers: PeerInfo list * newPeers: PeerInfo list

type RaftState =
    { Role: NodeRole
      Persistent: PersistentState
      Volatile: VolatileState
      LeaderState: LeaderState option
      VotesReceived: Set<NodeId>
      CurrentLeader: NodeId option
      Config: NodeConfig
      ConfigPhase: ConfigPhase
      NonVotingPeers: PeerInfo list }

type IPersistence =
    abstract member Save: PersistentState -> unit
    abstract member Load: unit -> PersistentState option

module State =
    let init config loadedState =
        let persistent =
            match loadedState with
            | Some p -> p
            | None ->
                { CurrentTerm = 0L
                  VotedFor = None
                  Log = Log.empty
                  Snapshot = None
                  SessionTable = Map.empty }

        let configPhase, updatedConfig =
            let latestChange =
                persistent.Log
                |> Map.toSeq
                |> Seq.choose (fun (_, entry) ->
                    if entry.Command.StartsWith ConfigChange.ConfigCommandPrefix then
                        ConfigChange.parse entry.Command
                    else
                        None)
                |> Seq.tryLast

            match latestChange with
            | Some(JointChange(oldPeers, newPeers)) ->
                let unionPeers = List.append oldPeers newPeers |> List.distinct
                JointPhase(oldPeers, newPeers), { config with Peers = unionPeers }
            | Some(FinalChange _)
            | None -> SinglePhase, config

        { Role = Follower
          Persistent = persistent
          Volatile = { CommitIndex = 0L; LastApplied = 0L }
          LeaderState = None
          VotesReceived = Set.empty
          CurrentLeader = None
          Config = updatedConfig
          ConfigPhase = configPhase
          NonVotingPeers = [] }

    let initLeaderState state =
        let newLog = Log.append state.Persistent.CurrentTerm "" state.Persistent.Log

        let allPeers =
            List.append state.Config.Peers state.NonVotingPeers
            |> List.distinctBy (fun p -> p.Id)

        let leaderState =
            { NextIndex = allPeers |> List.map (fun p -> p.Id, 1L) |> Map.ofList
              MatchIndex = allPeers |> List.map (fun p -> p.Id, 0L) |> Map.ofList }

        { state with
            Role = Leader
            Persistent = { state.Persistent with Log = newLog }
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

    let takeSnapshot lastAppliedIndex lastAppliedTerm data state =
        let newLog = Log.trim lastAppliedIndex lastAppliedTerm state.Persistent.Log

        let snapshot: Snapshot =
            { LastIncludedIndex = lastAppliedIndex
              LastIncludedTerm = lastAppliedTerm
              StateMachineData = data }

        { state with
            Persistent =
                { state.Persistent with
                    Log = newLog
                    Snapshot = Some snapshot }
            Volatile =
                { state.Volatile with
                    CommitIndex = max state.Volatile.CommitIndex lastAppliedIndex
                    LastApplied = max state.Volatile.LastApplied lastAppliedIndex } }

    let updateSessionTable clientId seqNum state =
        { state with
            Persistent =
                { state.Persistent with
                    SessionTable = state.Persistent.SessionTable |> Map.add clientId seqNum } }

    let updateConfig peers state =
        { state with
            Config = { state.Config with Peers = peers }
            ConfigPhase = SinglePhase }

    let enterJointConsensus oldPeers newPeers state =
        let unionPeers = List.append oldPeers newPeers |> List.distinct

        { state with
            ConfigPhase = JointPhase(oldPeers, newPeers)
            Config = { state.Config with Peers = unionPeers } }

    let exitJointConsensus newPeers state =
        let newState =
            { state with
                ConfigPhase = SinglePhase
                Config = { state.Config with Peers = newPeers } }

        let leaderExplicitlyRemoved =
            state.Config.Peers |> List.exists (fun p -> p.Id = state.Config.NodeId)
            && not (newPeers |> List.exists (fun p -> p.Id = state.Config.NodeId))

        if leaderExplicitlyRemoved then
            { newState with
                Role = Follower
                LeaderState = None
                CurrentLeader = None
                VotesReceived = Set.empty }
        else
            newState

    let hasQuorum supporters state =
        match state.ConfigPhase with
        | SinglePhase ->
            let total = List.length state.Config.Peers + 1
            Set.count supporters >= total / 2 + 1
        | JointPhase(oldPeers, newPeers) ->
            let oldIds =
                oldPeers
                |> List.map (fun p -> p.Id)
                |> Set.ofList
                |> Set.add state.Config.NodeId

            let newIds =
                newPeers
                |> List.map (fun p -> p.Id)
                |> Set.ofList
                |> Set.add state.Config.NodeId

            let inOld = Set.intersect supporters oldIds |> Set.count
            let inNew = Set.intersect supporters newIds |> Set.count
            inOld >= Set.count oldIds / 2 + 1 && inNew >= Set.count newIds / 2 + 1
