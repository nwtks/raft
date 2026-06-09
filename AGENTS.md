# AGENTS.md

This file provides guidance for AI agents working in this repository.

## Policy

- **Do not** include information that can be understood by reading the codebase (file names, function signatures, type definitions, etc.).
- **Do not** duplicate information that already exists in `README.md`.

Prefer documenting conventions, design rationale, non-obvious constraints, and cross-cutting concerns that are not apparent from the source code or the README.

- **Record design trade-offs** in the `Design Trade-offs` section whenever a significant architectural decision is made — include the rationale, the alternatives considered, and the pros/cons.

## Tech Stack

- **Language**: F# on .NET 10.0
- **Solution file**: `Raft.slnx`
- **Serialization**: `System.Text.Json` with custom `RaftMessageConverter` and `OptionConverterFactory` (see `Serialization.fs`)
- **Test framework**: xunit.v3 with Coverlet for coverage

## Cross-platform
The entire codebase, including test code, must run on both Windows and Linux.
  - All file paths must use relative paths or `System.IO.Path` utilities; never hardcode path separators (`\` or `/`).
  - Never use P/Invoke, platform-specific environment variables, or OS version checks.
  - TCP/IP code uses only `System.Net.Sockets` (no platform-specific socket options).
  - Persistence uses `System.IO.File.Move` with the `overwrite` parameter (supported on both platforms since .NET Core 3.0).
  - Threading uses `System.Threading.Timer` and `MailboxProcessor` (cross-platform).

## Architecture

Injected dependencies:
- `ITransport`   (default: `TcpTransport`)  — [defined in `NodeTypes.fs`, impl in `Transport.fs`]
- `IPersistence` (default: `FilePersistence`) — [defined in `State.fs`, impl in `Persistence.fs`]
- `onApply: LogEntry -> unit`  — state machine callback
- `onInstallSnapshot: string -> unit`  — snapshot callback (called async to avoid blocking the agent loop)
- `onGetSnapshotData: unit -> string`  — callback to obtain current state machine snapshot data

State is **immutable**. Every handler returns a new `RaftState`; the agent loop threads it through via tail-recursive `agentLoop`. Never mutate `RaftState` directly.

`PersistentState` (`CurrentTerm`, `VotedFor`, `Log`, `Snapshot`, `SessionTable`, `LastConfigIndex`) must be flushed to disk **before** replying to any RPC or timeout. The `saveIfChanged` helper in `NodeUtil.fs` enforces this — always call it after state transitions. `saveIfChanged` is *idempotent* (no-op if `Persistent` field unchanged) so it is safe to call unconditionally.

### Timer Handling — `TimerAction` Declarative Pattern

- **Election timer**: one-shot `System.Threading.Timer`; fires `ElectionTimeout` into the inbox.
- **Heartbeat timer**: one-shot timer; fires `HeartbeatTimeout` into the inbox.
- Timers are controlled declaratively via `TimerAction` (`Keep | Reset | Stop`) carried in `MessageResult.ElectionAction` / `MessageResult.HeartbeatAction`.
- Handlers never manipulate `System.Threading.Timer` directly; they return a `TimerAction` value. `NodeTimer.applyElectionAction` / `applyHeartbeatAction` in `postProcess` translate actions into actual `Timer.Change()` / `createTimer` calls.
- `NodeTimer.getTimerActionsOnRoleChange` returns the correct `TimerAction` pair based on old/new role and `sendReply`. It also calls `broadcastHeartbeat` on Leader promotion.
- Timer mutation (`Change`, `createTimer`) is centralized in `NodeTimer`, not scattered across handlers.

## Transport Wire Format

Messages are serialized as JSON using custom `System.Text.Json` converters (`RaftMessageConverter` in `Serialization.fs`). The union case name is the discriminator (`"Case"` property) with the payload in a `"Fields"` array. Messages are framed with a **4-byte big-endian length prefix** followed by the UTF-8 JSON payload, so there is no hard size limit.

TCP connection timeout for outbound messages is **3 000 ms** (hardcoded in `Transport.sendMessage`).

`ClientCommand` (the `AsyncReplyChannel` case) is never sent over the wire — it is only posted locally to the inbox.

## Test Suite

**`IntegrationTests.fs`** exercises end-to-end Raft scenarios (leader election, log replication, log inconsistency recovery, split-brain, stale leader rejection) by calling the pure `Election`, `Replication`, and `State` module functions directly — **no TCP sockets, no `RaftNode` actor, no real timers**. These tests are fast and deterministic.

**`TransportTests.fs`** is the only test file that opens real TCP sockets on loopback. It may conflict if ports are already in use; run in isolation when needed.

Maintain high unit test coverage (target: ≥ 80% line coverage).

## Coding Conventions

- **Prefer immutable state** — All `RaftState` transitions return a new state; never mutate in place.
- **Use pure functions** — Algorithm logic (election, replication, log operations) goes in pure functions with no side effects; separate I/O into injected dependencies (`ITransport`, `IPersistence`).
- **Favor expressions over statements** — Use `match` expressions, `if`/`then`/`else`, and pattern matching instead of imperative control flow.
- **Leverage discriminated unions** — Model domain concepts (messages, roles, configuration phases, timer actions, pending reads) with DUs for exhaustiveness checking.
- **Use `[<TailCall>]` on recursive functions** that loop (e.g., `agentLoop`, `findFirstIdx`) to prevent stack overflows.
- **Declarative timer control** — Use `TimerAction` (`Keep | Reset | Stop`) instead of passing raw `System.Threading.Timer` values through `MessageResult`. Handlers declare *intent*; `postProcess` applies it.
- **Uniform handler dispatch** — All message handlers return `MessageResult { State; ElectionAction; HeartbeatAction; PendingReads }`. The `agentLoop` is a pure dispatcher (match → handler → `postProcess` → tail-call). No inline logic except `Shutdown` cleanup and `GetState` pass-through.
- Do not introduce new external NuGet packages without checking existing dependencies in the `.fsproj` files first.

## Design Trade-offs

### ITransport Async<unit> vs Task<unit>

`ITransport` interface was unified to `Async<unit>` (previously `Task<unit>`).

**Rationale**: `NodeAgent.agentLoop` is written with `async { }`, so unifying the entire codebase under a single async model avoids impedance mismatch.

**Trade-offs**:
- ✅ `NodeUtil.sendAsync` simplified from `Task.ContinueWith` to `async { try...with } |> Async.Start`, making error handling declarative
- ✅ Tests (`MockTransport`) no longer need `Task.FromResult` — `async { return () }` is cleaner
- ❌ Every call to a .NET `Task<T>` / `ValueTask<T>` API (`ConnectAsync`, `WriteAsync`, etc.) requires `.AsTask() |> Async.AwaitTask`. F# 9 does not have `Async.AwaitValueTask`, so explicit conversion is unavoidable
- ❌ C# consumers cannot use the interface directly (acceptable since this is a pure F# project)

### MessageResult + postProcess Pattern

`MessageResult` (`{ State; ElectionAction; HeartbeatAction; PendingReads }`) is the universal return type for all handlers; `postProcess` applies it to `NodeContext`.

**Rationale**: Before this pattern, `agentLoop` inlined timer handling, pending-read processing, and Config tracking between handler calls. Duplicated logic across message branches.

**Trade-offs**:
- ✅ `agentLoop` is now a pure dispatcher — match msg, call handler, `postProcess`, tail-call. No inline logic except Shutdown/GetState.
- ✅ Adding a new message type requires only a new handler module + one line in `agentLoop`.
- ✅ Timer mutation, Config tracking, and PendingRead processing are unified in one place (`postProcess`), not duplicated across branches.
- ❌ `MessageResult` creation in each handler is slightly more verbose than directly mutating `ctx` fields.
- ❌ `postProcess` is an additional indirection layer; understanding the full message lifecycle requires tracing through handler → `MessageResult` → `postProcess`.

### TimerAction Declarative Timer Control

`TimerAction` (`Keep | Reset | Stop`) replaced raw `System.Threading.Timer option` in `MessageResult`.

**Rationale**: Previously, handlers created/reset/stopped timers directly and passed the resulting `Timer` references through `MessageResult`. Timer logic was scattered across all handler modules, and the timer's logical state (armed/disarmed) was implicit in the opaque `Timer` object.

**Trade-offs**:
- ✅ Timer mutations are centralized in `NodeTimer.applyElectionAction` / `applyHeartbeatAction`, called from `postProcess`. Handlers only declare *intent*.
- ✅ `TimerAction` values are testable — can assert `ElectionAction = Reset` without needing real timers.
- ✅ Adding a new timer action (e.g., `ResetWithInterval`) requires only adding a DU case and updating `apply*Action`.
- ❌ Every handler must explicitly set both `ElectionAction` and `HeartbeatAction` (usually `Keep`), adding boilerplate.
- ❌ Two-phase pattern (action → apply) is one more indirection than direct mutation.

### Config Phase Recovery: LastConfigIndex Field

`recoverConfigPhase` was changed from full-log scan (O(n)) to `Map.tryFind` lookup (O(log n)) by adding `LastConfigIndex: LogIndex` to `PersistentState`.

**Rationale**: On restart, the old code scanned the entire log `Map` to find the last config change entry. As the log grows, this becomes unnecessary I/O.

**Trade-offs**:
- ✅ Recovery is O(log n) regardless of log size.
- ✅ No legacy format support — `LastConfigIndex = 0` means `SinglePhase, config` with no scan.
- ❌ New persisted field means state files from before this change lose the config phase on restart (acceptable: protocol catches up via RPC).
- ❌ `takeSnapshot` must reset `LastConfigIndex` if the snapshot index exceeds it, adding complexity to the snapshot path.

### Log.truncateAndAppend Optimization

`truncateAndAppend` was rewritten from `Map.toSeq` + `Map.ofSeq` to a `Map.remove` loop.

**Rationale**: The old approach serialized the entire log to a sequence (O(n)) and rebuilt a new map (O(k log k)). The new approach only removes conflicting entries (O(m log n)).

**Trade-offs**:
- ✅ Only the conflicting suffix is removed; the rest of the map is unchanged.
- ✅ Same O(m log n) worst case as the old approach, but better constant factors when the conflict is near the end.
- ❌ Slightly more code than the one-liner `Map.toSeq` + `Map.ofSeq`.
