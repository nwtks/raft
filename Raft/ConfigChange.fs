namespace Raft

type ConfigChangeData =
    | JointChange of oldPeers: PeerInfo list * newPeers: PeerInfo list
    | FinalChange of peers: PeerInfo list

module ConfigChange =
    [<Literal>]
    let ConfigCommandPrefix = "__raft_config:"

    let serialize =
        function
        | JointChange(oldPeers, newPeers) ->
            {| t = "j"
               o = oldPeers
               n = newPeers |}
            |> System.Text.Json.JsonSerializer.Serialize
        | FinalChange peers -> {| t = "f"; p = peers |} |> System.Text.Json.JsonSerializer.Serialize

    let parseArray (json: string) =
        System.Text.Json.JsonSerializer.Deserialize<PeerInfo list> json
        |> FinalChange
        |> Some

    let parseTagged (root: System.Text.Json.JsonElement) =
        match root.TryGetProperty "t" with
        | true, tag ->
            match tag.GetString() with
            | "j" ->
                let oldP =
                    System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>(root.GetProperty("o").GetRawText())

                let newP =
                    System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>(root.GetProperty("n").GetRawText())

                JointChange(oldP, newP) |> Some
            | "f" ->
                System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>(root.GetProperty("p").GetRawText())
                |> FinalChange
                |> Some
            | _ -> None
        | false, _ -> None

    let parse (command: string) =
        let json = command.Substring ConfigCommandPrefix.Length

        try
            use doc = System.Text.Json.JsonDocument.Parse json
            let root = doc.RootElement

            match root.ValueKind with
            | System.Text.Json.JsonValueKind.Array -> parseArray json
            | System.Text.Json.JsonValueKind.Object -> parseTagged root
            | _ -> None
        with _ ->
            None
