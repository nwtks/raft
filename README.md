# Raft implemented in F#

A purely functional, highly robust implementation of the [Raft Consensus Algorithm](https://raft.github.io/) written from scratch in F#.

## Overview

This project implements the core mechanics of Raft — Leader Election and Log Replication — using F#'s strong type system, immutability, and actor model (`MailboxProcessor`). It focuses on thread-safety and correctness, avoiding complex locking mechanisms by serializing all state updates through message passing.

## Features

- **Leader Election:** Randomized election timeouts, `RequestVote` RPC handling, and dynamic leader promotion on majority vote.
- **Log Replication:** `AppendEntries` RPC handling, log conflict resolution via `mergeEntries`, and commit index advancement.
- **Actor Model Design:** Non-blocking, thread-safe state machine using F#'s `MailboxProcessor` (`Node.fs`). State is fully immutable; each message handler returns a new `RaftState` threaded through a tail-recursive `agentLoop`.
- **TCP Transport Layer:** Custom JSON-based RPC serialization over raw TCP sockets with length-prefixed framing and hand-written `System.Text.Json` converters (`Transport.fs`, `Serialization.fs`).
- **Crash Recovery & Persistence:** Atomic disk persistence for `PersistentState` (`CurrentTerm`, `VotedFor`, `Log`) via a `.tmp`-swap write, ensuring state integrity across restarts (`Persistence.fs`).
- **Interactive Cluster Demo:** A 3-node Key-Value Store (KVS) cluster demo with `put`, `get`, `state`, and `quit` commands (`Raft.App`).
- **Comprehensive Test Suite:** Unit and integration tests covering election, log operations, replication, actor behaviour, transport, and persistence.

## Project Structure

```text
Raft/               # Core library (OutputType: Library)
├── Types.fs        # Core types (NodeId, Term, LogIndex, LogEntry, NodeRole, RPC messages, NodeConfig)
├── Log.fs          # Pure immutable log operations & conflict resolution
├── State.fs        # RaftState, PersistentState, VolatileState, LeaderState; IPersistence interface
├── Election.fs     # RequestVote RPC & quorum logic
├── Replication.fs  # AppendEntries RPC & commit index advancement
├── Node.fs         # MailboxProcessor actor, ITransport interface, timer management
├── Transport.fs    # Asynchronous TCP transport (TcpTransport)
└── Persistence.fs  # Atomic disk persistence (FilePersistence)

Raft.App/           # CLI entry point (references Raft library)
└── Program.fs      # 3-node interactive KVS demo

Raft.Tests/         # xunit.v3 test suite
├── LogTests.fs
├── StateTests.fs
├── ElectionTests.fs
├── ReplicationTests.fs
├── IntegrationTests.fs  # Pure-function end-to-end scenarios (no TCP, no actors)
├── NodeTests.fs         # RaftNode actor tests (MockTransport / MockPersistence)
├── TransportTests.fs    # TcpTransport loopback tests
└── PersistenceTests.fs
```

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

## Getting Started

### 1. Build the project

```bash
dotnet build
```

### 2. Run the Multi-Node Cluster Demo

Start 3 nodes in separate terminals. Each node listens on a fixed loopback port:

| Node ID | Port |
|---------|------|
| 0 | 5000 |
| 1 | 5001 |
| 2 | 5002 |

```bash
dotnet run --project Raft.App -- --node 0  # Terminal A
dotnet run --project Raft.App -- --node 1  # Terminal B
dotnet run --project Raft.App -- --node 2  # Terminal C
```

Timing defaults: election timeout 1 500–3 000 ms, heartbeat 500 ms.

Once all nodes are running they will perform leader election automatically. From any terminal you can enter the following commands:

| Command | Description |
|---------|-------------|
| `put <key> <value>` | Submit a write to the cluster (only accepted by the Leader) |
| `get <key>` | Read from the local node's in-memory KVS state |
| `state` | Print the node's current role, term, commit index, and log length |
| `quit` / `q` | Exit |

> **💡 Try Crash Recovery:** Terminate a node with `Ctrl+C` and restart it. The node will automatically recover its persisted `CurrentTerm` and `Log` from its state file and seamlessly rejoin the cluster. Persistence files are written to the **current working directory** as `state_0.json`, `state_1.json`, `state_2.json`.

### 3. Run Tests & Measure Coverage

```bash
# Run all tests with coverage report
dotnet test
```
