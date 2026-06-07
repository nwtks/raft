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
    let recoverConfigPhase (log: Map<LogIndex, LogEntry>) config =
        let latestChange =
            log
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
        | Some(FinalChange peers) -> SinglePhase, { config with Peers = peers }
        | None -> SinglePhase, config

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

        let configPhase, updatedConfig = recoverConfigPhase persistent.Log config

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

    let updateTerm term state =
        if term > state.Persistent.CurrentTerm then
            { state with
                Persistent =
                    { state.Persistent with
                        CurrentTerm = term
                        VotedFor = None }
                Role = Follower
                LeaderState = None
                VotesReceived = Set.empty }
        else
            state

    let updateLog log state =
        { state with
            Persistent = { state.Persistent with Log = log } }

    let updateLogAndCommit log commitIndex state =
        { state with
            Persistent = { state.Persistent with Log = log }
            Volatile =
                { state.Volatile with
                    CommitIndex = commitIndex } }

    let updateCommitIndex commitIndex state =
        { state with
            Volatile =
                { state.Volatile with
                    CommitIndex = commitIndex } }

    let updateLastApplied lastApplied state =
        { state with
            Volatile =
                { state.Volatile with
                    LastApplied = lastApplied } }

    let followLeader leaderTerm leaderId state =
        let newState = updateTerm leaderTerm state

        { newState with
            Role = Follower
            CurrentLeader = Some leaderId }

    let updateLeaderState nextIndex matchIndex state =
        match state.LeaderState with
        | Some ls ->
            { state with
                LeaderState =
                    Some
                        { ls with
                            NextIndex = nextIndex
                            MatchIndex = matchIndex } }
        | None -> state

    let recordVote candidateId state =
        { state with
            Persistent =
                { state.Persistent with
                    VotedFor = Some candidateId } }

    let addVoteReceived nodeId state =
        { state with
            VotesReceived = state.VotesReceived |> Set.add nodeId }

    let takeSnapshot lastIndex lastTerm data state =
        let newLog = Log.trim lastIndex lastTerm state.Persistent.Log

        let snapshot =
            { LastIncludedIndex = lastIndex
              LastIncludedTerm = lastTerm
              StateMachineData = data }

        { state with
            Persistent =
                { state.Persistent with
                    Log = newLog
                    Snapshot = Some snapshot }
            Volatile =
                { state.Volatile with
                    CommitIndex = max state.Volatile.CommitIndex lastIndex
                    LastApplied = max state.Volatile.LastApplied lastIndex } }

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

    let exitJointConsensus peers state =
        let newState =
            { state with
                ConfigPhase = SinglePhase
                Config = { state.Config with Peers = peers } }

        let leaderExplicitlyRemoved =
            state.Config.Peers |> List.exists (fun p -> p.Id = state.Config.NodeId)
            && not (peers |> List.exists (fun p -> p.Id = state.Config.NodeId))

        if leaderExplicitlyRemoved then
            { newState with
                Role = Follower
                LeaderState = None
                CurrentLeader = None
                VotesReceived = Set.empty }
        else
            newState

    let hasQuorumJoint supporters oldPeers newPeers nodeId =
        let oldIds = oldPeers |> List.map (fun p -> p.Id) |> Set.ofList |> Set.add nodeId
        let newIds = newPeers |> List.map (fun p -> p.Id) |> Set.ofList |> Set.add nodeId
        let inOld = Set.intersect supporters oldIds |> Set.count
        let inNew = Set.intersect supporters newIds |> Set.count
        inOld >= Set.count oldIds / 2 + 1 && inNew >= Set.count newIds / 2 + 1

    let hasQuorum supporters state =
        match state.ConfigPhase with
        | SinglePhase ->
            let total = List.length state.Config.Peers + 1
            Set.count supporters >= total / 2 + 1
        | JointPhase(oldPeers, newPeers) -> hasQuorumJoint supporters oldPeers newPeers state.Config.NodeId
