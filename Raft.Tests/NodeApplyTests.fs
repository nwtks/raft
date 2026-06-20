module Raft.Tests.NodeApplyTests

open Xunit
open Raft
open TestHelpers

let c2 = [ { Id = 2; Host = ""; Port = 0 } ]
let c3 = [ { Id = 3; Host = ""; Port = 0 } ]

let makeJointEntry index term =
    let cmd =
        ConfigChange.ConfigCommandPrefix + ConfigChange.serialize (JointChange(c2, c3))

    { Index = index
      Term = term
      Command = cmd
      ClientId = None
      SeqNum = None }

let makeFinalEntry index term =
    let cmd = ConfigChange.ConfigCommandPrefix + ConfigChange.serialize (FinalChange c2)

    { Index = index
      Term = term
      Command = cmd
      ClientId = None
      SeqNum = None }

[<Fact>]
let ``NodeApply.applyConfigChangeEntry enters JointPhase on JointChange`` () =
    let state = State.init dummyConfig None
    let entry = makeJointEntry 1L 1L
    let result = NodeApply.applyConfigChangeEntry entry state

    match result.ConfigPhase with
    | JointPhase(oldPeers, newPeers) ->
        Assert.Contains(c2.Head, oldPeers)
        Assert.Contains(c3.Head, newPeers)
    | _ -> Assert.Fail "Expected JointPhase" |> ignore

[<Fact>]
let ``NodeApply.applyConfigChangeEntry with session info updates SessionTable before JointPhase`` () =
    let state = State.init dummyConfig None

    let entry =
        { makeJointEntry 1L 1L with
            ClientId = Some "client-1"
            SeqNum = Some 5L }

    let result = NodeApply.applyConfigChangeEntry entry state
    let sessionSeq = result.Persistent.SessionTable |> Map.tryFind "client-1"
    Assert.Equal(Some 5L, sessionSeq)

    match result.ConfigPhase with
    | JointPhase _ -> ()
    | _ -> Assert.Fail "Expected JointPhase" |> ignore

[<Fact>]
let ``NodeApply.applyConfigChangeEntry exits JointPhase on FinalChange`` () =
    let mutable state = State.init dummyConfig None
    state <- State.enterJointConsensus c2 c3 state
    let entry = makeFinalEntry 2L 1L
    let result = NodeApply.applyConfigChangeEntry entry state
    Assert.Equal(SinglePhase, result.ConfigPhase)
    Assert.DoesNotContain(result.Config.Peers, (fun p -> p.Id = 3))

[<Fact>]
let ``NodeApply.applyConfigChangeEntry handles old array format (parseArray fallback)`` () =
    let state = State.init dummyConfig None
    let json = """[{"Id":2,"Host":"127.0.0.1","Port":5002}]"""
    let cmd = ConfigChange.ConfigCommandPrefix + json

    let entry =
        { Index = 1L
          Term = 1L
          Command = cmd
          ClientId = None
          SeqNum = None }

    let result = NodeApply.applyConfigChangeEntry entry state
    Assert.Equal(SinglePhase, result.ConfigPhase)
    Assert.Single result.Config.Peers |> ignore
    Assert.Equal(2, result.Config.Peers.Head.Id)

[<Fact>]
let ``NodeApply.applyConfigChangeEntry ignores entry when parse returns None and deserialization yields null`` () =
    let state = State.init dummyConfig None
    let cmd = ConfigChange.ConfigCommandPrefix + "null"

    let entry =
        { Index = 1L
          Term = 1L
          Command = cmd
          ClientId = None
          SeqNum = None }

    let result = NodeApply.applyConfigChangeEntry entry state
    Assert.Equal(SinglePhase, result.ConfigPhase)
    Assert.Equal<PeerInfo list>(dummyConfig.Peers, result.Config.Peers)

[<Fact>]
let ``NodeApply.applyNormalEntry applies command and invokes callback when not duplicate`` () =
    let applied = ResizeArray<string>()
    let state = State.init dummyConfig None
    let entry = createEntry 1L 1L "cmd1"
    let result = NodeApply.applyNormalEntry (fun e -> applied.Add e.Command) entry state
    Assert.Single applied |> ignore
    Assert.Equal("cmd1", applied.[0])
    Assert.Same(state, result)

[<Fact>]
let ``NodeApply.applyNormalEntry updates session table with new session info`` () =
    let applied = ResizeArray<string>()
    let state = State.init dummyConfig None

    let entry =
        { createEntry 1L 1L "cmd1" with
            ClientId = Some "client-1"
            SeqNum = Some 3L }

    let result = NodeApply.applyNormalEntry (fun e -> applied.Add e.Command) entry state
    Assert.Single applied |> ignore

    let sessionSeq = result.Persistent.SessionTable |> Map.tryFind "client-1"
    Assert.Equal(Some 3L, sessionSeq)

[<Fact>]
let ``NodeApply.applyNormalEntry skips callback and keeps state on duplicate session`` () =
    let applied = ResizeArray<string>()
    let mutable state = State.init dummyConfig None
    state <- State.updateSessionTable "client-1" 10L state

    let entry =
        { createEntry 1L 1L "cmd1" with
            ClientId = Some "client-1"
            SeqNum = Some 5L }

    let result = NodeApply.applyNormalEntry (fun e -> applied.Add e.Command) entry state
    Assert.Empty applied

    let sessionSeq = result.Persistent.SessionTable |> Map.tryFind "client-1"
    Assert.Equal(Some 10L, sessionSeq)
    Assert.Same(state, result)

[<Fact>]
let ``NodeApply.applyNormalEntry without ClientId/SeqNum skips session table update`` () =
    let applied = ResizeArray<string>()
    let mutable state = State.init dummyConfig None
    state <- State.updateSessionTable "client-1" 10L state
    let entry = createEntry 1L 1L "cmd1"
    let result = NodeApply.applyNormalEntry (fun e -> applied.Add e.Command) entry state
    Assert.Single applied |> ignore
    Assert.Equal("cmd1", applied.[0])

    let sessionSeq = result.Persistent.SessionTable |> Map.tryFind "client-1"
    Assert.Equal(Some 10L, sessionSeq)

[<Fact>]
let ``NodeApply.loopApplyCommitted skips over missing log entries`` () =
    let mutable state = State.init dummyConfig None
    let log = logFromList [ createEntry 1L 1L "cmd1"; createEntry 3L 1L "cmd3" ]

    state <-
        { state with
            Persistent = { state.Persistent with Log = log } }

    state <- State.updateCommitIndex 3L state
    state <- State.updateLastApplied 0L state
    let applied = ResizeArray<string>()
    let _, _ = NodeApply.loopApplyCommitted (fun e -> applied.Add e.Command) state 0L
    Assert.Equal(2, applied.Count)
    Assert.Equal("cmd1", applied.[0])
    Assert.Equal("cmd3", applied.[1])
