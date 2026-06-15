# Design Trade-offs

This document catalogs significant design trade-offs made in this Raft implementation.
Each entry describes the decision, the alternatives considered, and the resulting pros/cons.

See [docs/architecture.md](architecture.md) for the full architecture description.
See [docs/gotchas.md](gotchas.md) for common mistakes and gotchas.

---

## 1. ITransport: `Async<unit>` vs `Task<unit>`

`ITransport` methods return `Async<unit>` rather than `Task<unit>`.

**Rationale**: The entire `agentLoop` and handler chain uses F# `async { }` computation expressions. A single async model avoids impedance mismatch.

**Trade-offs**:
- ✅ `NodeUtil.sendAsync` uses `async { try...with } |> Async.Start` — declarative error handling instead of `Task.ContinueWith`
- ✅ Test `MockTransport` returns `async { return () }` — no `Task.FromResult` needed
- ❌ Some `ValueTask<T>` BCL APIs (`TcpClient.ConnectAsync`) require `.AsTask() |> Async.AwaitTask`; others (`NetworkStream.ReadAsync`, `TcpListener.AcceptTcpClientAsync`) are directly awaitable with `Async.AwaitTask` in F# 10 — the pattern is inconsistent
- ❌ C# consumers cannot use the interface directly (acceptable: pure F# project)

---

## 2. MessageResult + postProcess Pattern

All handlers (except `Shutdown`) return `MessageResult` (`{ State; ElectionAction; HeartbeatAction; PendingReads }`), and `postProcess` applies the result to `NodeContext`.

**Rationale**: Before this pattern, timer handling, pending-read processing, and Config tracking were inlined in `agentLoop` and duplicated across message branches.

**Trade-offs**:
- ✅ `agentLoop` is a thin dispatcher — only `Shutdown` is handled directly; all other messages route through `handleMessage` → `MessageResult` → `postProcess`
- ✅ Adding a new message type requires only a handler + one line in `handleMessage`
- ✅ Timer mutation, Config tracking, and PendingRead processing are unified in `postProcess`
- ❌ Every handler constructs a `MessageResult` record — more verbose than mutating `ctx` fields directly
- ❌ One extra indirection: handler → `MessageResult` → `postProcess` → next iteration

---

## 3. TimerAction: Declarative Timer Control

`TimerAction` (`Keep | Reset | Stop`) replaces raw `System.Threading.Timer option` mutation in handlers.

**Rationale**: Previously, handlers created/reset/stopped timers directly. Timer management was scattered, and the timer's logical state was implicit in the opaque `Timer` object.

**Trade-offs**:
- ✅ Timer mutations are centralized in `NodeTimer.applyElectionAction` / `applyHeartbeatAction`, called from `postProcess`
- ✅ `TimerAction` values are directly testable (e.g., assert `ElectionAction = Reset`) without real timers
- ✅ Adding a new timer action requires only a DU case + updating the `apply*Action` function
- ❌ Every handler explicitly sets both `ElectionAction` and `HeartbeatAction` — usually `Keep`
- ❌ Two-phase pattern (declare intent → apply) is one more indirection

---

## 4. Reply Callbacks: `('T -> unit)` Functions vs `AsyncReplyChannel<'T>`

`NodeMessage` cases carry reply callbacks as plain `('T -> unit)` functions rather than the built-in `AsyncReplyChannel<'T>`.

**Rationale**: `AsyncReplyChannel<'T>` ties the DU to `MailboxProcessor` and forces generic type parameters on each case. Plain functions keep the DU simple and non-generic.

**Trade-offs**:
- ✅ `NodeMessage` has no generic type parameters — simpler pattern matching and debugger inspection
- ✅ Tests provide `ignore` or assertion lambdas without constructing reply channels
- ✅ Reply callbacks are explicit in the DU definition — visible at a glance
- ❌ Callers write `agent.PostAndReply(fun ch -> ClientCommand(cmd, None, None, ch.Reply))` — slightly more verbose than with `AsyncReplyChannel<'T>`
- ❌ No compile-time distinction between reply callbacks and other function-typed parameters

---

## 5. Config Phase Recovery: LastConfigIndex Field

`recoverConfigPhase` uses `LastConfigIndex: LogIndex` in `PersistentState` for O(log n) lookup instead of scanning the entire log map.

**Rationale**: On restart, the old code scanned every log entry to find the last config change. As the log grows, this scan becomes unnecessary I/O.

**Trade-offs**:
- ✅ Recovery is O(log n) regardless of log size — `Map.tryFind` on the log map
- ✅ `LastConfigIndex = Log.initialLogIndex` cleanly means "no config change" — no scan at all
- ❌ New persisted field — state files from before this change lose config phase on restart (acceptable: protocol catches up via RPC)
- ❌ `takeSnapshot` must reset `LastConfigIndex` if the snapshot index exceeds it

---

## 6. Log.truncateAndAppend: `Map.remove` Loop

`truncateAndAppend` removes conflicting entries via a tail-recursive `Map.remove` loop instead of serializing the entire log through `Map.toSeq` + `Map.ofSeq`.

**Rationale**: The old approach serialized the entire log (O(n)) and rebuilt a new map (O(k log k)). The new approach only removes the conflicting suffix.

**Trade-offs**:
- ✅ Only the conflicting suffix is touched; the rest of the map shares structure
- ✅ Better constant factors when the conflict is near the tail
- ❌ More code than the one-liner `Map.toSeq >> Seq.filter >> Map.ofSeq`

---

## 7. Cyclomatic Complexity via Coverlet

Cyclomatic complexity is enforced via Coverlet's IL branch-count metric (from `coverage.cobertura.xml`) rather than a Roslyn-based source analyzer.

**Rationale**: `Microsoft.CodeAnalysis.Metrics` does not support F#. Coverlet, already in the build pipeline for coverage, computes branch counts from compiled IL that correlate closely with McCabe's complexity.

**Trade-offs**:
- ✅ No new dependencies — Coverlet is already a project dependency
- ✅ F# compatible — operates on IL, not source
- ✅ Single `dotnet test` run produces both coverage and complexity data
- ✅ MSBuild integration via `CheckComplexityAfterCoverage` target
- ❌ IL branch counts can differ from source-level McCabe for F# constructs (closures, computation expressions)
- ❌ `async { }` and `task { }` computation expressions compile to state machine classes with implicit branching (success/failure/cancellation paths), inflating complexity beyond what the source code suggests — this is the primary reason `Transport.fs` and test files using `task { }` have higher Coverlet complexity
- ❌ F# closures compile to nested classes whose `Invoke` method names don't map directly to source function names (e.g., `NodeRaft/handleRaftMessage@19`)
- ❌ `dotnet test` exit code may not reflect the complexity check in all SDK versions; CI should use `dotnet fsi scripts/check-complexity.fsx`

---

## 8. MailboxProcessor: Unbounded Queue

`MailboxProcessor<NodeMessage>` has no bounded capacity or backpressure mechanism.

**Rationale**: F#'s `MailboxProcessor` is simple, well-tested, and sufficient for typical Raft cluster sizes. Bounded queues add complexity (what to do on overflow — drop? block?).

**Trade-offs**:
- ✅ Zero configuration — `MailboxProcessor.Start()` with no capacity arguments
- ✅ No deadlock risk from queue backpressure (handler never blocks waiting to enqueue)
- ❌ A slow or partitioned node can accumulate unbounded messages in the inbox
- ❌ No visibility into queue depth at runtime

---

## 9. Fire-and-Forget `sendAsync` Error Handling

`NodeUtil.sendAsync` uses `Async.Start` for fire-and-forget execution. Send failures are logged but the caller receives no notification.

**Rationale**: Raft is designed to tolerate lost messages — retries naturally follow via heartbeat and election timeouts. Synchronous sends would couple message processing to network latency.

**Trade-offs**:
- ✅ Handlers never block on network I/O — the actor loop stays responsive
- ✅ Raft's retry model handles transient failures transparently
- ❌ Persistent send failures (e.g., wrong peer address) only surface as log messages — no error reported to the caller
- ❌ Tests using `MockTransport` never exercise failure paths; real network errors are only tested in `TransportTests.fs`

---

## 10. Map-based Log Storage

The log is stored as `Map<LogIndex, LogEntry>` (an immutable sorted map) rather than a `ResizeArray` or linked list.

**Rationale**: F# `Map` provides structural sharing for immutable updates, O(log n) lookup, and works naturally with the functional style used throughout the codebase.

**Trade-offs**:
- ✅ Structural sharing — `Log.append` returns a new map that shares most nodes with the old one
- ✅ `Map.tryFind` is O(log n) — acceptable for typical log sizes
- ✅ Natural fit with F# immutable records and `saveIfChanged` structural comparison
- ❌ O(log n) per access vs O(1) for array indexing — noticeable only for very large logs
- ❌ No efficient append-only optimization — each `Map.add` creates new internal nodes

---

## 11. printfn Logging

The codebase uses `printfn` directly rather than a structured logging library (`ILogger`, Serilog, etc.).

**Rationale**: Zero external dependencies. The output is human-readable and sufficient for debugging and development. Production deployments can redirect stdout.

**Trade-offs**:
- ✅ No dependencies, no configuration, no DI wiring
- ✅ Every module has a local `let log msg = printfn "[Tag] %s" msg` wrapper
- ❌ No log levels (Info/Warn/Error), no structured fields, no filtering
- ❌ Heavily concurrent or production environments would benefit from timestamped, level-filtered, structured output
- ❌ Test output includes real log lines (mitigated by `MockTransport` not logging)

---

## 12. Config Change Legacy Format Support

`ConfigChange.parse` supports two serialization formats: tagged objects (`{"t":"j"/"f", "o":[...], "n":[...]}`) and plain arrays (`[...]`). The apply path in `NodeApply.applyConfigChangeEntry` also handles the fallback where `parse` returns `None` by deserializing the payload as `PeerInfo list` directly. A guard clause at the top of `parse` rejects commands shorter than `ConfigCommandPrefix` to prevent `ArgumentOutOfRangeException` from malformed input.

**Rationale**: The plain-array format was the original; tagged objects were added for joint consensus. The fallback in the apply path handles deserialization errors or unparseable payloads as single-phase config changes. The length guard ensures `ConfigChange.parse` is self-robust regardless of caller-side validation.

**Trade-offs**:
- ✅ Backward compatibility with log entries written before tagged format
- ✅ Robust — unparseable config commands are treated as single-phase `updateConfig` rather than crashing
- ❌ Three code paths for config change parsing (tagged, array, fallback) — more test surface
- ❌ The apply-path fallback (`System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>`) duplicates the array parsing logic of `parseArray`
