# Design Trade-offs

This document catalogs significant design trade-offs made in this Raft implementation.

See [docs/architecture.md](architecture.md) for the full architecture description.
See [docs/gotchas.md](gotchas.md) for common mistakes and gotchas.

---

## ITransport Async<unit> vs Task<unit>

`ITransport` interface was unified to `Async<unit>` (previously `Task<unit>`).

**Rationale**: `NodeAgent.agentLoop` is written with `async { }`, so unifying the entire codebase under a single async model avoids impedance mismatch.

**Trade-offs**:
- ✅ `NodeUtil.sendAsync` simplified from `Task.ContinueWith` to `async { try...with } |> Async.Start`, making error handling declarative
- ✅ Tests (`MockTransport`) no longer need `Task.FromResult` — `async { return () }` is cleaner
- ❌ Calls to .NET `ValueTask<T>` APIs (`ConnectAsync`, `WriteAsync`) require `.AsTask() |> Async.AwaitTask` conversion. `Task<T>` APIs (`ReadAsync`) and some `ValueTask<T>` APIs (`AcceptTcpClientAsync`) are awaitable directly with `Async.AwaitTask` in F# 10, but the conversion pattern is inconsistently needed across the BCL
- ❌ C# consumers cannot use the interface directly (acceptable since this is a pure F# project)

---

## MessageResult + postProcess Pattern

`MessageResult` (`{ State; ElectionAction; HeartbeatAction; PendingReads }`) is the universal return type for all handlers; `postProcess` applies it to `NodeContext`.

**Rationale**: Before this pattern, `agentLoop` inlined timer handling, pending-read processing, and Config tracking between handler calls. Duplicated logic across message branches.

**Trade-offs**:
- ✅ `agentLoop` is mostly a dispatcher — most messages route to handler modules returning `MessageResult`, which `postProcess` then applies. Only `Shutdown`, `GetState`, `LinearizableRead` (both Leader and non-Leader branches), and `TakeSnapshot` reply have inline logic.
- ✅ Adding a new message type typically requires only a new handler module + one line in `agentLoop`.
- ✅ Timer mutation, Config tracking, and PendingRead processing are unified in one place (`postProcess`), not duplicated across branches.
- ❌ `MessageResult` creation in each handler is slightly more verbose than directly mutating `ctx` fields.
- ❌ `postProcess` is an additional indirection layer; understanding the full message lifecycle requires tracing through handler → `MessageResult` → `postProcess`.
- ❌ Some messages (`LinearizableRead` Leader branch, `TakeSnapshot`) are partially inlined in `agentLoop` before/after calling their handler modules, breaking the uniform dispatch pattern.

---

## TimerAction Declarative Timer Control

`TimerAction` (`Keep | Reset | Stop`) replaced raw `System.Threading.Timer option` in `MessageResult`.

**Rationale**: Previously, handlers created/reset/stopped timers directly and passed the resulting `Timer` references through `MessageResult`. Timer logic was scattered across all handler modules, and the timer's logical state (armed/disarmed) was implicit in the opaque `Timer` object.

**Trade-offs**:
- ✅ Timer mutations are centralized in `NodeTimer.applyElectionAction` / `applyHeartbeatAction`, called from `postProcess`. Handlers only declare *intent*.
- ✅ `TimerAction` values are testable — can assert `ElectionAction = Reset` without needing real timers.
- ✅ Adding a new timer action (e.g., `ResetWithInterval`) requires only adding a DU case and updating `apply*Action`.
- ❌ Every handler must explicitly set both `ElectionAction` and `HeartbeatAction` (usually `Keep`), adding boilerplate.
- ❌ Two-phase pattern (action → apply) is one more indirection than direct mutation.

---

## Config Phase Recovery: LastConfigIndex Field

`recoverConfigPhase` was changed from full-log scan (O(n)) to `Map.tryFind` lookup (O(log n)) by adding `LastConfigIndex: LogIndex` to `PersistentState`.

**Rationale**: On restart, the old code scanned the entire log `Map` to find the last config change entry. As the log grows, this becomes unnecessary I/O.

**Trade-offs**:
- ✅ Recovery is O(log n) regardless of log size.
- ✅ No legacy format support — `LastConfigIndex = 0` means `SinglePhase, config` with no scan.
- ❌ New persisted field means state files from before this change lose the config phase on restart (acceptable: protocol catches up via RPC).
- ❌ `takeSnapshot` must reset `LastConfigIndex` if the snapshot index exceeds it, adding complexity to the snapshot path.

---

## Log.truncateAndAppend Optimization

`truncateAndAppend` was rewritten from `Map.toSeq` + `Map.ofSeq` to a `Map.remove` loop.

**Rationale**: The old approach serialized the entire log to a sequence (O(n)) and rebuilt a new map (O(k log k)). The new approach only removes conflicting entries (O(m log n)).

**Trade-offs**:
- ✅ Only the conflicting suffix is removed; the rest of the map is unchanged.
- ✅ Same O(m log n) worst case as the old approach, but better constant factors when the conflict is near the end.
- ❌ Slightly more code than the one-liner `Map.toSeq` + `Map.ofSeq`.

---

## Cyclomatic Complexity via Coverlet Coverage Data

The codebase uses Coverlet's `complexity` attribute (emitted in `coverage.cobertura.xml`) as an automated cyclomatic complexity gate, rather than a separate Roslyn-based analyzer.

**Rationale**: `Microsoft.CodeAnalysis.Metrics` (Roslyn-based) does not support F# projects. Coverlet, already in the build pipeline for coverage, computes a branch-count-based metric from the compiled IL that correlates closely with McCabe's cyclomatic complexity. Reusing this data avoids introducing a new tool or analysis pass.

**Trade-offs**:
- ✅ No new dependencies — Coverlet is already a project dependency.
- ✅ F# compatible — Coverlet operates on compiled IL, not source language.
- ✅ One unified report — coverage and complexity from a single `dotnet test` run.
- ✅ MSBuild integration — the `CheckComplexityAfterCoverage` target hooks into Coverlet's `GenerateCoverageResultAfterTest` target.
- ❌ Coverlet's complexity is IL branch-count-based, which can differ from source-level McCabe CC for F# constructs (closures, computation expressions).
- ❌ F# closures compiled as nested classes produce `Invoke` methods whose names don't directly map to the source function name (e.g., `NodeRaft/handleRaftMessage@19`).
- ❌ `dotnet test` exit code may not reflect the complexity check error in all SDK versions — CI scripts should use the explicit command `dotnet fsi scripts/check-complexity.fsx` for reliable enforcement.

---

## NodeMessage Reply Types: `('T -> unit)` Functions vs `AsyncReplyChannel<'T>`

`NodeMessage` DU cases that carry reply callbacks use `('T -> unit)` function types (e.g., `ClientCommand of command: string * clientId: string option * seqNum: int64 option * (ClientCommandResult -> unit)`) rather than F#'s built-in `AsyncReplyChannel<'T>`.

**Rationale**: `AsyncReplyChannel<'T>` ties the message DU to the `MailboxProcessor` abstraction and forces each case to be generic in its reply type. Plain function types keep the DU simple (all cases are non-generic), make the type easier to inspect in debuggers, and allow reply callbacks to be any function — not just the channel's `Reply` method.

**Trade-offs**:
- ✅ `NodeMessage` DU has no generic type parameters — simpler, easier to pattern-match
- ✅ Tests can provide plain `ignore` or assertion lambdas as reply callbacks without constructing reply channels
- ✅ Reply callbacks are explicit in the DU case definition — you can see at a glance which messages expect a reply
- ❌ `PostAndReply` callers must manually wire `ch.Reply` as the callback (e.g., `agent.PostAndReply(fun ch -> ClientCommand(cmd, None, None, ch.Reply))`), which is slightly more verbose than the `agent.PostAndReply(fun ch -> ClientCommand(cmd))` that `AsyncReplyChannel<'T>` would allow
- ❌ No compile-time distinction between reply callbacks and other function parameters — a `(bool -> unit)` could be either a reply or a mutation callback
