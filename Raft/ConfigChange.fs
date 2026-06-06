namespace Raft

type ConfigChangeData =
    | JointChange of oldPeers: PeerInfo list * newPeers: PeerInfo list
    | FinalChange of peers: PeerInfo list

module ConfigChange =
    [<Literal>]
    let ConfigCommandPrefix = "__raft_config:"

    let serialize =
        function
        | FinalChange peers ->
            let obj = {| t = "f"; p = peers |}
            System.Text.Json.JsonSerializer.Serialize obj
        | JointChange(oldPeers, newPeers) ->
            let obj =
                {| t = "j"
                   o = oldPeers
                   n = newPeers |}

            System.Text.Json.JsonSerializer.Serialize obj

    let parse (command: string) =
        let json = command.Substring ConfigCommandPrefix.Length

        try
            use doc = System.Text.Json.JsonDocument.Parse json
            let root = doc.RootElement

            match root.ValueKind with
            | System.Text.Json.JsonValueKind.Array ->
                let peers = System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>(json)
                Some(FinalChange peers)
            | System.Text.Json.JsonValueKind.Object ->
                match root.TryGetProperty "t" with
                | true, tag ->
                    match tag.GetString() with
                    | "j" ->
                        let oldP =
                            System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>(
                                root.GetProperty("o").GetRawText()
                            )

                        let newP =
                            System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>(
                                root.GetProperty("n").GetRawText()
                            )

                        Some(JointChange(oldP, newP))
                    | "f" ->
                        let peers =
                            System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>(
                                root.GetProperty("p").GetRawText()
                            )

                        Some(FinalChange peers)
                    | _ -> None
                | false, _ -> None
            | _ -> None
        with _ ->
            None
