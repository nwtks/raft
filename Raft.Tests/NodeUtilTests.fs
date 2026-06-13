module Raft.Tests.NodeUtilTests

open Xunit
open Raft
open TestHelpers

[<Fact>]
let ``NodeUtil.sendAsync handles synchronous exception from transport`` () =
    let syncThrowTransport =
        { new ITransport with
            member _.SendMessage _ _ =
                raise (System.InvalidOperationException("sync failure"))

            member _.StartListener _ _ _ = async { return () } }

    let peer: PeerInfo = { Id = 99; Host = ""; Port = 0 }

    let msg: RaftMessage =
        RequestVoteMsg
            { CandidateTerm = 1L
              CandidateId = 99
              LastLogIndex = 0L
              LastLogTerm = 0L }

    NodeUtil.sendAsync syncThrowTransport peer msg
    System.Threading.Thread.Sleep 200

[<Fact>]
let ``NodeUtil.sendAsync handles asynchronous exception from transport`` () =
    let asyncThrowTransport =
        { new ITransport with
            member _.SendMessage _ _ = async { failwith "async failure" }
            member _.StartListener _ _ _ = async { return () } }

    let peer: PeerInfo = { Id = 99; Host = ""; Port = 0 }

    let msg: RaftMessage =
        RequestVoteMsg
            { CandidateTerm = 1L
              CandidateId = 99
              LastLogIndex = 0L
              LastLogTerm = 0L }

    NodeUtil.sendAsync asyncThrowTransport peer msg
    System.Threading.Thread.Sleep 200
