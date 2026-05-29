# AGENTS.md

This file provides guidance for AI agents working in this repository.

## Tech Stack

- **Language**: F# on .NET 8.0
- **Solution file**: `Raft.slnx`
- **Serialization**: `System.Text.Json` + `FSharp.SystemTextJson` (`JsonFSharpConverter`)
- **Test framework**: xUnit with Coverlet for coverage

## Project Layout

| Project | Type | Role |
|---|---|---|
| `Raft/` | Library | Core Raft algorithm — all algorithm logic lives here |
| `Raft.App/` | Executable | CLI demo: 3-node KVS cluster |
| `Raft.Tests/` | Test | xUnit test suite |

### Key files in `Raft/`

| File | Responsibility |
|---|---|
| `Types.fs` | Core types: `NodeId`, `Term`, `LogEntry`, `NodeRole`, all RPC message types, `NodeConfig` |
| `Log.fs` | Pure log operations (append, conflict resolution, index lookup) |
| `State.fs` | `RaftState`, `PersistentState`, `VolatileState`, `LeaderState`; `IPersistence` interface |
| `Election.fs` | `RequestVote` RPC creation and handling; quorum logic |
| `Replication.fs` | `AppendEntries` RPC creation and handling; commit index advancement |
| `Node.fs` | `RaftNode` actor (`MailboxProcessor`); `ITransport` interface; main message loop |
| `Transport.fs` | `TcpTransport`: async TCP listener + fire-and-forget sender using JSON over raw TCP |
| `Persistence.fs` | `FilePersistence`: atomic disk writes to `state_{id}.json` via a `.tmp` swap |

**F# compilation order matters.** Files inside each `.fsproj` must be listed in dependency order (e.g., `Types.fs` before everything else). When adding a new file, insert it at the correct position in the `<Compile>` list.

## Architecture

```
RaftNode (MailboxProcessor)
  ├── ITransport  (injected — default: TcpTransport)
  ├── IPersistence (injected — default: FilePersistence)
  └── onApply: LogEntry -> unit  (state machine callback, injected by caller)
```

State is **immutable**. Every handler returns a new `RaftState`; the agent loop threads it through via tail-recursive `agentLoop`. Never mutate `RaftState` directly.

`PersistentState` (`CurrentTerm`, `VotedFor`, `Log`) must be flushed to disk **before** replying to any RPC. The `saveIfChanged` helper in `Node.fs` enforces this — always call it after state transitions.

## Transport Wire Format

Messages are serialized as JSON using `FSharp.SystemTextJson` with `JsonFSharpConverter`. The union case name is the discriminator (default FSharp.SystemTextJson behaviour). The buffer size is 65 536 bytes per message; do not send messages larger than this without changing `Transport.fs`.

TCP connection timeout for outbound messages is **3 000 ms** (hardcoded in `Transport.sendMessage`).

## Test Suite

| File | Coverage area |
|---|---|
| `ElectionTests.fs` | `Election` module |
| `ReplicationTests.fs` | `Replication` module |
| `LogTests.fs` | `Log` module |
| `StateTests.fs` | `State` module |
| `NodeTests.fs` | `RaftNode` actor behaviour |
| `PersistenceTests.fs` | `FilePersistence` |
| `TransportTests.fs` | `TcpTransport` |
| `IntegrationTests.fs` | Multi-node end-to-end scenarios |

Integration tests spin up real `RaftNode` instances on loopback ports and assert consensus behaviour. They are slower and may conflict if ports are already in use; run them in isolation when needed.

## Coding Conventions

- All algorithm logic goes in `Raft/` (pure functions where possible, no I/O side effects).
- Side-effecting concerns (network, disk) are hidden behind `ITransport` / `IPersistence` interfaces and injected at the `RaftNode` constructor — keep them mockable.
- Prefer `async {}` computation expressions for async work inside the actor; use `task {}` for the transport layer (interop with .NET `Task`-based APIs).
- Use `[<TailCall>]` on recursive agent-loop functions to prevent stack overflows.
- Do not introduce new external NuGet packages without checking existing dependencies in the `.fsproj` files first.
