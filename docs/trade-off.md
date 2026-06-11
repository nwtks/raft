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
- ❌ Every call to a .NET `Task<T>` / `ValueTask<T>` API (`ConnectAsync`, `WriteAsync`, etc.) requires `.AsTask() |> Async.AwaitTask`. F# 10 does not have `Async.AwaitValueTask`, so explicit conversion is unavoidable
- ❌ C# consumers cannot use the interface directly (acceptable since this is a pure F# project)

---

## MessageResult + postProcess Pattern

`MessageResult` (`{ State; ElectionAction; HeartbeatAction; PendingReads }`) is the universal return type for all handlers; `postProcess` applies it to `NodeContext`.

**Rationale**: Before this pattern, `agentLoop` inlined timer handling, pending-read processing, and Config tracking between handler calls. Duplicated logic across message branches.

**Trade-offs**:
- ✅ `agentLoop` is now a pure dispatcher — match msg, call handler, `postProcess`, tail-call. No inline logic except Shutdown/GetState.
- ✅ Adding a new message type requires only a new handler module + one line in `agentLoop`.
- ✅ Timer mutation, Config tracking, and PendingRead processing are unified in one place (`postProcess`), not duplicated across branches.
- ❌ `MessageResult` creation in each handler is slightly more verbose than directly mutating `ctx` fields.
- ❌ `postProcess` is an additional indirection layer; understanding the full message lifecycle requires tracing through handler → `MessageResult` → `postProcess`.

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
