# Raft implemented in F#

A purely functional, highly robust implementation of the [Raft Consensus Algorithm](https://raft.github.io/) written from scratch in F#.

## Overview

This project implements the core mechanics of Raft — Leader Election and Log Replication — using F#'s strong type system, immutability, and actor model (`MailboxProcessor`). It focuses on thread-safety and correctness, avoiding complex locking mechanisms by serializing all state updates through message passing.

## Project Structure

| Project | Type | Description |
|---------|------|-------------|
| `Raft/` | Library | Core Raft consensus algorithm (election, replication, log, state machine, transport, persistence) |
| `Raft.App/` | CLI App | Key-Value Store cluster demo with interactive REPL and admin HTTP listener |
| `Raft.Tests/` | Test Suite | Unit and integration tests (xUnit + Coverlet) |

## Features

| Feature | Description |
|---------|-------------|
| Leader Election | Randomized election timeouts, `RequestVote` RPC, dynamic leader promotion |
| Log Replication | `AppendEntries` RPC, log conflict resolution, commit advancement |
| Linearizable Reads | Quorum-based read index protocol |
| Snapshot & Compaction | `InstallSnapshot` RPC, automatic log compaction |
| Cluster Membership | Two-phase joint consensus for dynamic cluster changes (add/remove peers) |
| Non-Voting Members | Catch-up mechanism — new nodes join as non-voting members before promotion |
| Auto-Join Protocol | New nodes discover the cluster via admin listener and request membership automatically |
| Session De-duplication | Server-side session tracking prevents duplicate command execution |

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

Start nodes in separate terminals. Each node listens on a fixed loopback port.

**3-node cluster example:**

| Node ID | Port |
|---------|------|
| 0 | 5000 |
| 1 | 5001 |
| 2 | 5002 |

```bash
dotnet run --project Raft.App -- --node 0 --port 5000 --peer 1 127.0.0.1 5001 --peer 2 127.0.0.1 5002
dotnet run --project Raft.App -- --node 1 --port 5001 --peer 0 127.0.0.1 5000 --peer 2 127.0.0.1 5002
dotnet run --project Raft.App -- --node 2 --port 5002 --peer 0 127.0.0.1 5000 --peer 1 127.0.0.1 5001
```

Timing defaults: election timeout 1 500–3 000 ms, heartbeat 500 ms.

Once all nodes are running they will perform leader election automatically. From any terminal you can enter:

| Command | Description |
|---------|-------------|
| `put <key> <value>` | Submit a write to the cluster (only accepted by the Leader) |
| `get <key>` | Read from the cluster with linearizable consistency (only works on the Leader) |
| `state` | Print the node's current role, term, leader, commit index, last applied, log count |
| `dump` | Print full Raft state dump (peers, snapshot, session table, leader state) |
| `log` | Print all log entries |
| `cluster` | Show cluster membership (peers, non-voting members, leader) |
| `snapshot` | Take a manual snapshot of the state machine |
| `election` | Force an election timeout |
| `heartbeat` | Force a heartbeat broadcast |
| `add-peer <id> <host> <port>` | Add a new peer as a non-voting member |
| `remove-peer <id>` | Remove a peer from the cluster |
| `help` | Print command reference |
| `quit` / `q` | Exit |

#### Adding a new node to a running cluster

Start a node whose ID is **not** among the existing peers. The new node automatically contacts a known peer's admin listener (raft port + 10000) and requests to join as a non-voting member:

```bash
dotnet run --project Raft.App -- --node 3 --port 5003 --peer 0 127.0.0.1 5000 --peer 1 127.0.0.1 5001 --peer 2 127.0.0.1 5002
```

The leader adds it as a non-voting member, replicates log entries for catch-up, and once caught up automatically promotes it to a full voting member via joint consensus.

> **💡 Try Crash Recovery:** Terminate a node with `Ctrl+C` and restart it. The node will automatically recover its persisted `CurrentTerm`, `VotedFor`, `Log`, `Snapshot`, and `SessionTable` from its state file and seamlessly rejoin the cluster. Persistence files are written to the **current working directory** as `state_{nodeId}.json`.
