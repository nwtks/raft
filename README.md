# Raft implemented in F#

A purely functional, highly robust implementation of the [Raft Consensus Algorithm](https://raft.github.io/) written from scratch in F#.

## Overview

This project implements the core mechanics of Raft—Leader Election and Log Replication—using F#'s strong type system, immutability, and actor model (`MailboxProcessor`). It focuses on thread-safety and correctness, avoiding complex locking mechanisms by serializing state updates through message passing.

## Features

- **Leader Election:** Randomized election timeouts, RequestVote RPC handling, and dynamic leader promotion.
- **Log Replication:** AppendEntries RPC handling, log conflict resolution, and commit index advancement.
- **Actor Model Design:** Non-blocking, thread-safe state machine utilizing F#'s `MailboxProcessor` (`Node.fs`).
- **TCP Transport Layer:** Custom JSON-based RPC serialization over raw TCP sockets using F# Discriminated Unions support (`Transport.fs`).
- **Crash Recovery & Persistence:** Strict atomic disk persistence for `PersistentState` (using `System.Text.Json`), ensuring state integrity across node restarts.
- **Interactive Cluster Demo:** A multi-node Key-Value Store (KVS) cluster demo included out-of-the-box (`Program.fs`).
- **Comprehensive Test Suite:** Unit and integration tests covering election, log operations, replication, and transport layers.

## Project Structure

```text
src/Raft/
├── Types.fs        # Core types (NodeRole, LogEntry, RaftMessage)
├── Log.fs          # Immutable log operations & conflict resolution
├── State.fs        # Raft state management (Persistent/Volatile)
├── Election.fs     # RequestVote RPC & quorum logic
├── Replication.fs  # AppendEntries RPC & commit index advancement
├── Node.fs         # MailboxProcessor-based Actor implementation
├── Transport.fs    # Asynchronous TCP transport layer
├── Persistence.fs  # Disk-based state persistence
└── Program.fs      # Multi-node interactive CLI demo

tests/Raft.Tests/   # xUnit-based Test Suite
```

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)

## Getting Started

### 1. Build the project

```bash
dotnet build
```

### 2. Run the Multi-Node Cluster Demo

You can run 3 nodes locally on different terminals to observe Raft in action.

**Node 1 (Terminal A):**
```bash
dotnet run --project src/Raft -- --node 0
```

**Node 2 (Terminal B):**
```bash
dotnet run --project src/Raft -- --node 1
```

**Node 3 (Terminal C):**
```bash
dotnet run --project src/Raft -- --node 2
```

Once all nodes are running, they will perform leader election. You can then submit commands (e.g., `put x 100`) from the Leader's terminal and watch the command replicate to Follower nodes.

> **💡 Try Crash Recovery:** Terminate a node with `Ctrl+C` and restart it. The node will automatically recover its persisted `CurrentTerm` and `Log` from `state_{id}.json` and seamlessly rejoin the active cluster!

### 3. Run Tests & Measure Coverage

To execute the test suite and output an XML code coverage report (Cobertura format):

```bash
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
```

For a visual HTML report, you can optionally install [ReportGenerator](https://danielpalme.github.io/ReportGenerator/) and run:
```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:"tests/Raft.Tests/coverage.cobertura.xml" -targetdir:"coveragereport" -reporttypes:Html
```

---

*This is an educational implementation aimed at understanding distributed systems and highly concurrent F# design patterns.*
