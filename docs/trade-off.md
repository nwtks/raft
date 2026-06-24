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

## 7. Cyclomatic Complexity: Hybrid Approach (Coverlet + Source Analysis)

Cyclomatic complexity is enforced via a hybrid approach: Coverlet's coverage XML (`coverage.cobertura.xml`) provides method/function discovery and line mapping, while a standalone F# script (`scripts/check-complexity.fsx`) calculates complexity from source code using keyword-based analysis.

**Rationale**: `Microsoft.CodeAnalysis.Metrics` does not support F#. Pure IL-level metrics from Coverlet are available but don't always match source-level McCabe complexity for F# constructs like computation expressions and closures. A source-level approach gives more predictable results for an F# codebase. The Coverlet XML is reused for method discovery to avoid re-parsing all source files independently.

**Trade-offs**:
- ✅ No new dependencies — Coverlet (`coverlet.msbuild`) is already a test-project dependency; the script uses `System.IO`, `System.Text.RegularExpressions`, and `System.Xml.Linq`
- ✅ Complexity matches source code structure, not IL compiler artifacts
- ✅ Single `dotnet test` run produces both coverage and complexity data
- ✅ MSBuild integration via `CheckComplexityAfterCoverage` target (in `Directory.Build.targets`) which runs the script after `GenerateCoverageResultAfterTest`
- ✅ Configurable thresholds via `Directory.Build.props` (`ComplexityThreshold`, `ComplexityWarnThreshold`)
- ❌ Regex-based keyword counting is heuristic — may under- or over-count in edge cases (e.g., `if` inside quoted expressions)
- ❌ Match-case counting (`|` patterns) uses indentation heuristics rather than AST-walking, potentially missing nested match expressions
- ❌ Boolean operator counting (`&&`/`||`) is limited to lines containing `if`/`elif`/`while`/`when`, which can miss compound conditions in other contexts
- ❌ The script must stay synchronized with actual F# syntax features; new constructs require updating the keyword patterns
- ❌ `dotnet test` exit code may not reflect the complexity check in all SDK versions; CI should use `dotnet fsi scripts/check-complexity.fsx` directly

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

The codebase uses `eprintfn` directly rather than a structured logging library (`ILogger`, Serilog, etc.).

**Rationale**: Zero external dependencies. The output is human-readable and sufficient for debugging and development. Stderr is used so log output does not interfere with application stdout.

**Trade-offs**:
- ✅ No dependencies, no configuration, no DI wiring
- ✅ Every module has a local `let log msg = eprintfn "[Tag] %s" msg` wrapper
- ❌ No log levels (Info/Warn/Error), no structured fields, no filtering
- ❌ Heavily concurrent or production environments would benefit from timestamped, level-filtered, structured output
- ❌ Test output includes real log lines (mitigated by `MockTransport` not logging)

---

## 12. Config Change Legacy Format Support

`ConfigChange.parse` supports two serialization formats: tagged objects (`{"t":"j"/"f", "o":[...], "n":[...]}`) and plain arrays (`[...]`). The apply path in `NodeApply.applyConfigChangeEntry` includes a null guard: when `parse` returns `None` and the raw deserialization yields `null`, the entry is logged and skipped rather than crashing.

**Rationale**: The plain-array format was the original; tagged objects were added for joint consensus. The null guard prevents a `NullReferenceException` from malformed payloads like `"null"` which `System.Text.Json` deserializes as `null` for a `PeerInfo list` (reference type). The length guard in `ConfigChange.parse` also rejects commands shorter than `ConfigCommandPrefix` to prevent `ArgumentOutOfRangeException`.

**Trade-offs**:
- ✅ Backward compatibility with log entries written before tagged format
- ✅ Robust — null payloads are logged and silently skipped; the node's peer configuration remains unchanged
- ❌ Three code paths for config change parsing (tagged, array, fallback) — more test surface
- ❌ The apply-path fallback (`System.Text.Json.JsonSerializer.Deserialize<PeerInfo list>`) duplicates the array parsing logic of `parseArray`

---

## 13. Per-RPC-Type Handler Functions vs Single Big `handleRaftMessage`

`NodeRaft.handleRaftMessage` was refactored from one function with 6 match branches into 6 dedicated handler functions (`handleRequestVote`, `handleRequestVoteResponse`, `handleAppendEntries`, `handleAppendEntriesResponse`, `handleInstallSnapshot`, `handleInstallSnapshotResponse`), with `handleRaftMessage` becoming a thin dispatcher. Each handler returns `RaftState * bool` (state + whether a reply was sent). Previously the tuple also included an `Async<unit> option` for after-effects, but `handleInstallSnapshot` now starts its `onInstallSnapshot` callback inline via `Async.Start`.

**Rationale**: The original function mixed responsibilities — each branch combined `saveIfChanged` (persistence I/O) with `sendResponse` (network I/O) and `afterEffect` (async spawn) with different patterns across branches. The `afterEffect` tuple element was only used by one handler but forced all handlers to carry it.

**Trade-offs**:
- ✅ Each handler function is independently responsible for exactly one RPC type and its I/O
- ✅ No closure captures — each handler explicitly takes `ctx` as a parameter
- ✅ The `afterEffect` async spawn is now local to `handleInstallSnapshot` where it belongs, not a cross-cutting tuple element
- ✅ Adding a new RPC type requires adding one handler function + one match case in the dispatcher
- ❌ More code overall (6 handlers + dispatcher vs 6 branches in one function)
- ❌ Slight duplication: most handlers call `saveIfChanged` explicitly, where the old `reply`/`noReply` closures handled it once each

---

## 14. `getTimerActionsOnRoleChange`: Pure Function vs Side-Effect Mix

`NodeTimer.getTimerActionsOnRoleChange` was refactored from a function that both computed timer actions AND broadcast heartbeats on leader promotion into a pure function that only computes timer actions. The broadcast was moved to the callers (`handleRaftRPC`, `handleLocalMessage`).

**Rationale**: A function named `getTimerActions*` should not have side effects. The heartbeat broadcast on leader promotion is a separate concern that should be visible at the call site.

**Trade-offs**:
- ✅ Pure function — testable without `ctx` or network I/O
- ✅ Callers explicitly handle leader promotion broadcast, making the flow visible
- ✅ No implicit side effects from a "query"-named function
- ❌ Callers must remember to broadcast on leader promotion (but the pattern is simple: `if oldRole <> Leader && newState.Role = Leader then broadcast`)
- ❌ Slight duplication: two callers have the same promotion broadcast check

---

## 15. Replaced `commitAndBroadcast` with Common `applyAndCompact` Pipeline

`NodeLocal.commitAndBroadcast` was removed. Its post-save pipeline (apply committed → auto-snapshot → finalize config) was extracted into `NodePromotion.applyAndCompact`, leaving the per-call-site I/O (save, broadcast) explicit at each call site (`appendAsLeader`, `handleRemovePeer`).

**Rationale**: The original function hid a 5-step pipeline behind a short, generic name. The caller had no visibility into which side effects were being executed without reading the helper. By extracting only the pure post-processing chain into a shared helper while keeping save+broadcast visible at the call site, the code balances DRY with explicitness.

**Trade-offs**:
- ✅ Each call site explicitly shows the I/O (save, broadcast) before the common post-processing
- ✅ No hidden side effects — readers can see save and broadcast at the call site
- ❌ Slight duplication: two call sites each repeat save+broadcast before calling `applyAndCompact`
- ❌ An extra module-level function (`NodePromotion.applyAndCompact`) to navigate

---

## 16. Extracted Pure `promoteReadyNonVotingPeers` vs Monolithic `tryPromoteNonVotingPeers`

`NodePromotion.tryPromoteNonVotingPeers` was split into a pure function `promoteReadyNonVotingPeers` (state transformation only) and the existing I/O wrapper.

**Rationale**: The original function mixed pure state computation (filtering ready peers, constructing new state, appending joint consensus) with I/O (save, broadcast, apply, snapshot, finalize) in a single dense block.

**Trade-offs**:
- ✅ Pure state transformation is testable without `ctx` or I/O mocks
- ✅ The I/O wrapper is clearly separated from the pure logic
- ✅ No behavior change — the I/O wrapper calls the pure function and adds side effects
- ❌ Two functions instead of one — more names to navigate
