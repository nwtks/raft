module Raft.Tests.ConfigChangeTests

open Xunit
open Raft

let peer1 =
    { Id = 1
      Host = "127.0.0.1"
      Port = 5001 }

let peer2 =
    { Id = 2
      Host = "127.0.0.1"
      Port = 5002 }

let peer3 =
    { Id = 3
      Host = "127.0.0.1"
      Port = 5003 }

[<Fact>]
let ``ConfigChange.serialize JointChange produces tagged JSON with t=j`` () =
    let oldPeers = [ peer1; peer2 ]
    let newPeers = [ peer1; peer2; peer3 ]
    let json = ConfigChange.serialize (JointChange(oldPeers, newPeers))
    Assert.Contains("\"t\":\"j\"", json)
    Assert.Contains("\"o\"", json)
    Assert.Contains("\"n\"", json)

[<Fact>]
let ``ConfigChange.serialize FinalChange produces tagged JSON with t=f`` () =
    let peers = [ peer1; peer2; peer3 ]
    let json = ConfigChange.serialize (FinalChange peers)
    Assert.Contains("\"t\":\"f\"", json)
    Assert.Contains("\"p\"", json)

[<Fact>]
let ``ConfigChange.parseArray parses legacy JSON array peer format`` () =
    let json =
        """[{"Id":1,"Host":"127.0.0.1","Port":5001},{"Id":2,"Host":"127.0.0.1","Port":5002}]"""

    let result = ConfigChange.parseArray json

    match result with
    | Some(FinalChange peers) ->
        Assert.Equal(2, peers.Length)
        Assert.Contains(peer1, peers)
        Assert.Contains(peer2, peers)
    | _ -> Assert.Fail "Expected FinalChange"

[<Fact>]
let ``ConfigChange.parseTagged parses JointChange tagged JSON`` () =
    let json =
        """{"t":"j","o":[{"Id":1,"Host":"127.0.0.1","Port":5001},{"Id":2,"Host":"127.0.0.1","Port":5002}],"n":[{"Id":1,"Host":"127.0.0.1","Port":5001},{"Id":2,"Host":"127.0.0.1","Port":5002},{"Id":3,"Host":"127.0.0.1","Port":5003}]}"""

    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement
    let result = ConfigChange.parseTagged root

    match result with
    | Some(JointChange(oldPeers, newPeers)) ->
        Assert.Equal(2, oldPeers.Length)
        Assert.Equal(3, newPeers.Length)
        Assert.Contains(peer1, oldPeers)
        Assert.Contains(peer2, oldPeers)
        Assert.Contains(peer3, newPeers)
    | _ -> Assert.Fail "Expected JointChange"

[<Fact>]
let ``ConfigChange.parseTagged parses FinalChange tagged JSON`` () =
    let json =
        """{"t":"f","p":[{"Id":1,"Host":"127.0.0.1","Port":5001},{"Id":2,"Host":"127.0.0.1","Port":5002},{"Id":3,"Host":"127.0.0.1","Port":5003}]}"""

    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement
    let result = ConfigChange.parseTagged root

    match result with
    | Some(FinalChange peers) -> Assert.Equal(3, peers.Length)
    | _ -> Assert.Fail "Expected FinalChange"

[<Fact>]
let ``ConfigChange.parseTagged returns None for unknown tag`` () =
    let json = """{"t":"x"}"""
    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement
    let result = ConfigChange.parseTagged root
    Assert.True result.IsNone

[<Fact>]
let ``ConfigChange.parseTagged returns None when no t property`` () =
    let json = """{"a":1}"""
    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement
    let result = ConfigChange.parseTagged root
    Assert.True result.IsNone

[<Fact>]
let ``ConfigChange.parse reads JointChange from full command string`` () =
    let oldPeers = [ peer1; peer2 ]
    let newPeers = [ peer1; peer2; peer3 ]
    let serialized = ConfigChange.serialize (JointChange(oldPeers, newPeers))
    let command = ConfigChange.ConfigCommandPrefix + serialized
    let result = ConfigChange.parse command

    match result with
    | Some(JointChange(op, np)) ->
        Assert.Equal(2, op.Length)
        Assert.Equal(3, np.Length)
    | _ -> Assert.Fail "Expected JointChange"

[<Fact>]
let ``ConfigChange.parse reads FinalChange from full command string`` () =
    let peers = [ peer1; peer2; peer3 ]
    let serialized = ConfigChange.serialize (FinalChange peers)
    let command = ConfigChange.ConfigCommandPrefix + serialized
    let result = ConfigChange.parse command

    match result with
    | Some(FinalChange ps) -> Assert.Equal(3, ps.Length)
    | _ -> Assert.Fail "Expected FinalChange"

[<Fact>]
let ``ConfigChange.parse round-trips JointChange`` () =
    let original = JointChange([ peer1; peer2 ], [ peer1; peer2; peer3 ])
    let serialized = ConfigChange.serialize original
    let command = ConfigChange.ConfigCommandPrefix + serialized
    let deserialized = ConfigChange.parse command

    match deserialized with
    | Some(JointChange(oldPeers, newPeers)) ->
        Assert.Equal(2, oldPeers.Length)
        Assert.Equal(3, newPeers.Length)
    | _ -> Assert.Fail "Expected JointChange"

[<Fact>]
let ``ConfigChange.parse round-trips FinalChange`` () =
    let original = FinalChange [ peer1; peer2; peer3 ]
    let serialized = ConfigChange.serialize original
    let command = ConfigChange.ConfigCommandPrefix + serialized
    let deserialized = ConfigChange.parse command

    match deserialized with
    | Some(FinalChange peers) -> Assert.Equal(3, peers.Length)
    | _ -> Assert.Fail "Expected FinalChange"

[<Fact>]
let ``ConfigChange.parse returns None for invalid JSON`` () =
    let command = ConfigChange.ConfigCommandPrefix + "{invalid}"
    let result = ConfigChange.parse command
    Assert.True result.IsNone

[<Fact>]
let ``ConfigChange.parse returns None when command is shorter than prefix`` () =
    let command = "__raft_config"
    let result = ConfigChange.parse command
    Assert.True result.IsNone

[<Fact>]
let ``ConfigChange.parse returns None when command equals prefix length (empty JSON)`` () =
    let command = ConfigChange.ConfigCommandPrefix
    let result = ConfigChange.parse command
    Assert.True result.IsNone

[<Fact>]
let ``ConfigChange.parse returns None for empty string`` () =
    let result = ConfigChange.parse ""
    Assert.True result.IsNone
