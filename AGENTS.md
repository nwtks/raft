# AGENTS.md

This file provides guidance for AI agents working in this repository.

## Tech Stack

- **Language**: F# on .NET 10.0
- **Solution file**: `Raft.slnx`
- **Serialization**: `System.Text.Json` with custom `RaftMessageConverter` and `OptionConverterFactory` (see `Serialization.fs`)
- **Test framework**: xunit.v3 with Coverlet for coverage

## Project Layout

| Project | Type | Role |
|---|---|---|
| `Raft/` | Library | Core Raft algorithm — all algorithm logic lives here |
| `Raft.App/` | Executable | CLI demo: 3-node KVS cluster |
| `Raft.Tests/` | Test | xUnit test suite |

### Key files in `Raft/`

| File | Responsibility |
|---|---|
| `Types.fs` | Core types: `NodeId`, `Term`, `LogIndex`, `LogEntry`, `NodeRole`, all RPC message/response types, `PeerInfo`, `NodeConfig` |
| `Log.fs` | Pure log operations: `append`, `mergeEntries` (conflict resolution), `entriesFrom`, `getEntry`, index/term lookup |
| `State.fs` | `PersistentState`, `VolatileState`, `LeaderState`, `RaftState`; `IPersistence` interface; pure state-transition helpers |
| `Election.fs` | `startElection`, `createRequestVote`, `handleRequestVote`, `handleVoteResponse`; quorum promotion to Leader |
| `Replication.fs` | `createAppendEntries`, `createHeartbeat`, `handleAppendEntries`, `handleAppendEntriesResponse`, `advanceCommitIndex`, `appendCommand` |
| `NodeTypes.fs` | `NodeMessage` discriminated union; `ITransport` interface; `NodeContext` record (threaded through agent loop) |
| `NodeTimer.fs` | Timer management: `resetElectionTimer`, `resetHeartbeatTimer`, `stopTimer`, `disposeTimer` |
| `NodeBroadcaster.fs` | Outbound message broadcasting: `broadcastRequestVote`, `broadcastHeartbeat`, `broadcastAppendEntries`, `sendAsync` |
| `NodeAgent.fs` | Core agent loop: `agentLoop`, `handleRaftMessage`, `handleLocalMessage`, `receiveElectionTimeout`, `receiveHeartbeatTimeout`, `applyCommitted`, `saveIfChanged` |
| `Node.fs` | `RaftNode` public API class: constructor, `SubmitCommand`, `GetState`, `AddPeer`, `RemovePeer`, `Dispose` |
| `Serialization.fs` | Custom `System.Text.Json` converters: `RaftMessageConverter`, `OptionConverterFactory` |
| `Transport.fs` | `TcpTransport`: async TCP listener + fire-and-forget sender using JSON over raw TCP |
| `Persistence.fs` | `FilePersistence`: atomic disk writes to `state_{id}.json` via a `.tmp` swap |

## Architecture

```
RaftNode (public API facade in Node.fs)
  └── MailboxProcessor<NodeMessage>  (agentLoop in NodeAgent.fs)
        ├── handleRaftMessage     (NodeAgent.fs)   — RequestVote / AppendEntries / InstallSnapshot
        ├── handleLocalMessage    (NodeAgent.fs)   — ClientCommand / AddPeer / RemovePeer / TakeSnapshot
        ├── receiveElectionTimeout (NodeAgent.fs)  — startElection + broadcast
        ├── receiveHeartbeatTimeout (NodeAgent.fs) — broadcastAppendEntries
        ├── applyCommitted        (NodeAgent.fs)   — apply entries to state machine
        ├── NodeBroadcaster       (NodeBroadcaster.fs) — outbound message fire-and-forget
        └── NodeTimer             (NodeTimer.fs)   — election / heartbeat timer management
```

Injected dependencies:
- `ITransport`   (default: `TcpTransport`)  — [defined in `NodeTypes.fs`, impl in `Transport.fs`]
- `IPersistence` (default: `FilePersistence`) — [defined in `State.fs`, impl in `Persistence.fs`]
- `onApply: LogEntry -> unit`  — state machine callback
- `onInstallSnapshot: string -> unit`  — snapshot callback (called async to avoid blocking the agent loop)

Internally, the agent loop threads a `NodeContext` record (defined in `NodeTypes.fs`) through each step:

```fsharp
type NodeContext =
    { Config: NodeConfig
      Transport: ITransport
      Persistence: IPersistence
      OnApply: LogEntry -> unit
      OnInstallSnapshot: string -> unit
      Inbox: MailboxProcessor<NodeMessage>
      State: RaftState
      ElectionTimer: Timer option
      HeartbeatTimer: Timer option
      CancellationTokenSource: CancellationTokenSource }
```

State is **immutable**. Every handler returns a new `RaftState`; the agent loop threads it through via tail-recursive `agentLoop`. Never mutate `RaftState` directly.

`PersistentState` (`CurrentTerm`, `VotedFor`, `Log`) must be flushed to disk **before** replying to any RPC. The `saveIfChanged` helper in `NodeAgent.fs` enforces this — always call it after state transitions.

### Timer Handling

- **Election timer**: one-shot `System.Threading.Timer`; reset on receiving any valid RPC, or on becoming Follower. Fires `ElectionTimeout` into the inbox.
- **Heartbeat timer**: one-shot timer; reset whenever the node is Leader. Fires `HeartbeatTimeout` into the inbox.
- Becoming Leader: stops the election timer, starts the heartbeat timer.
- Stepping down: stops the heartbeat timer, restarts the election timer.

## Transport Wire Format

Messages are serialized as JSON using custom `System.Text.Json` converters (`RaftMessageConverter` in `Serialization.fs`). The union case name is the discriminator (`"Case"` property) with the payload in a `"Fields"` array. Messages are framed with a **4-byte big-endian length prefix** followed by the UTF-8 JSON payload, so there is no hard size limit.

TCP connection timeout for outbound messages is **3 000 ms** (hardcoded in `Transport.sendMessage`).

`ClientCommand` (the `AsyncReplyChannel` case) is never sent over the wire — it is only posted locally to the inbox.

## Test Suite

| File | Coverage area |
|---|---|
| `LogTests.fs` | `Log` module — pure log operations |
| `StateTests.fs` | `State` module — init and state transitions |
| `ElectionTests.fs` | `Election` module — vote granting, quorum, term updates |
| `ReplicationTests.fs` | `Replication` module — AppendEntries, commit index |
| `IntegrationTests.fs` | Multi-step pure-function scenarios (no TCP, no actors) |
| `NodeTests.fs` | `RaftNode` actor behaviour using `MockTransport` / `MockPersistence` |
| `TransportTests.fs` | `TcpTransport` — real loopback TCP send/receive |
| `PersistenceTests.fs` | `FilePersistence` — save/load round-trip and overwrite |
| `SerializationTests.fs` | `RaftMessageConverter` / `OptionConverter` — JSON round-trips |

**`IntegrationTests.fs`** exercises end-to-end Raft scenarios (leader election, log replication, log inconsistency recovery, split-brain, stale leader rejection) by calling the pure `Election`, `Replication`, and `State` module functions directly — **no TCP sockets, no `RaftNode` actor, no real timers**. These tests are fast and deterministic.

**`TransportTests.fs`** is the only test file that opens real TCP sockets on loopback. It may conflict if ports are already in use; run in isolation when needed.

Maintain high unit test coverage (target: ≥ 80% line coverage).

## Coding Conventions

- All algorithm logic goes in `Raft/` (pure functions where possible, no I/O side effects).
- Side-effecting concerns (network, disk) are hidden behind `ITransport` / `IPersistence` interfaces and injected at the `RaftNode` constructor — keep them mockable.
- Prefer `async {}` computation expressions for async work inside the actor; use `task {}` for the transport layer (interop with .NET `Task`-based APIs).
- Use `[<TailCall>]` on recursive functions (`agentLoop`, `Log._merge`, `inputLoop` in App) to prevent stack overflows.
- Do not introduce new external NuGet packages without checking existing dependencies in the `.fsproj` files first.
