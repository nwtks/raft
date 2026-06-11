# Raft implemented in F#

A purely functional, highly robust implementation of the [Raft Consensus Algorithm](https://raft.github.io/) written from scratch in F#.

## Overview

This project implements the core mechanics of Raft — Leader Election and Log Replication — using F#'s strong type system, immutability, and actor model (`MailboxProcessor`). It focuses on thread-safety and correctness, avoiding complex locking mechanisms by serializing all state updates through message passing.

## Project Structure

| Project | Type | Description |
|---------|------|-------------|
| `Raft/` | Library | Core Raft consensus algorithm (election, replication, log, state machine, transport, persistence) |
| `Raft.App/` | CLI App | 3-node Key-Value Store cluster demo with interactive REPL |
| `Raft.Tests/` | Test Suite | 217 unit and integration tests (xUnit + Coverlet) |

## Features

| Feature | Description |
|---------|-------------|
| Leader Election | Randomized election timeouts, `RequestVote` RPC, dynamic leader promotion |
| Log Replication | `AppendEntries` RPC, log conflict resolution, commit advancement |
| Linearizable Reads | Quorum-based read index protocol |
| Snapshot & Compaction | `InstallSnapshot` RPC, automatic log compaction |
| Cluster Membership | Two-phase joint consensus for dynamic cluster changes |
| Session De-duplication | Client-side session tracking prevents duplicate execution |
| Interactive Demo | 3-node Key-Value Store cluster with REPL |
| Comprehensive Tests | Unit/integration tests (xUnit + Coverlet) |

See [docs/architecture.md](docs/architecture.md) for detailed descriptions of the actor model design, TCP transport layer, crash recovery, and other architectural details.

## Configuration

Each node is configured via `NodeConfig` (defined in `Types.fs`):

| Field | Description |
|-------|-------------|
| `NodeId` | Unique node identifier |
| `Host` | IP address to bind the transport listener |
| `Port` | TCP port for inter-node RPC communication |
| `Peers` | List of peer `PeerInfo` (id, host, port) |
| `ElectionTimeoutMinMs` / `ElectionTimeoutMaxMs` | Random election timeout range (ms) |
| `HeartbeatIntervalMs` | Leader heartbeat interval (ms) |
| `SnapshotAutoThreshold` | Log entries before auto-compaction triggers (0 = disabled) |

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

## Getting Started

### 1. Build the project

```bash
dotnet build
```

### 2. Run Tests & Measure Coverage

```bash
# Run all tests with coverage report
dotnet test
```

### 3. Run the Multi-Node Cluster Demo

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
| `get <key>` | Read from the cluster with linearizable consistency (only works on the Leader) |
| `state` | Print the node's current role, term, leader, commit index, last applied, and log count |
| `quit` / `q` | Exit |

> **💡 Try Crash Recovery:** Terminate a node with `Ctrl+C` and restart it. The node will automatically recover its persisted `CurrentTerm`, `VotedFor`, `Log`, `Snapshot`, and `SessionTable` from its state file and seamlessly rejoin the cluster. Persistence files are written to the **current working directory** as `state_0.json`, `state_1.json`, `state_2.json`.
