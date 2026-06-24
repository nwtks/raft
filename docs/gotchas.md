# Recurring Gotchas

This document catalogs recurring mistakes, non-obvious pitfalls, and subtle behaviors encountered when working with this Raft implementation.

See [docs/architecture.md](architecture.md) for the full architecture description.
See [docs/trade-off.md](trade-off.md) for design trade-off analyses.

---

## Forgetting `saveIfChanged`

Every handler that mutates `PersistentState` must call `NodeUtil.saveIfChanged` **before** replying to an RPC or timeout. The `saveIfChanged` helper is idempotent (no-op if the `Persistent` field is unchanged), so it is safe — and recommended — to call it unconditionally even when unsure.

**Why it's problematic**: Without the flush, a crash after replying but before the next persist cycle loses the state transition (e.g., a `VotedFor` record or term increment), potentially causing safety violations or duplicate votes on restart.

**How to avoid**: Always call `saveIfChanged ctx state` after any call to a `State.*` function that returns a new `RaftState`. When adding a new handler, add this call as the first line after computing the new state.

---

## Both `TimerAction` Fields Must Be Set

Every `MessageResult` must explicitly set both `ElectionAction` and `HeartbeatAction`. F# record syntax does not enforce that all fields are provided when using `with` mutations on a default value, and the default value of a DU is its first declared case — which may not be `Keep`.

**Why it's problematic**: Forgetting either field can cause the election timer to stop on a Follower (making it unable to start elections) or the heartbeat timer to keep firing on a non-Leader.

**How to avoid**: When creating a `MessageResult`, always write both fields explicitly. Do not rely on a "default" `MessageResult` and override only one field.

---

## `Async.Start` Fire-and-Forget Error Handling

`NodeUtil.sendAsync` uses `Async.Start` for fire-and-forget execution. Synchronous exceptions thrown by `transport.SendMessage` are immediately caught, logged, and replaced with `async { () }` (no-op). Asynchronous exceptions inside the `async` block are caught by `try...with` and logged. The `onInstallSnapshot` callback also uses `Async.Start` with error handling.

**Why it's problematic**: A failure to send a message to a peer (e.g., connection refused, timeout) is logged but the caller receives no notification of failure. The Raft protocol is designed to tolerate lost messages (retries via heartbeat), but silent failures can mask network partition issues during debugging.

**How to avoid**: For debugging, monitor the `[Node]` / `[Transport]` stderr output. In tests, use `MockTransport` which never fails — real network failures are only exercised in `TransportTests.fs`.

---

## `PostAndReply` Deadlock Risk

`MailboxProcessor.PostAndReply` blocks the calling thread until the agent processes the message and calls the reply callback. If called from **within** the agent loop (i.e., from a handler running inside `agentLoop`), it deadlocks — the agent is busy processing the current message and cannot process the new one.

**Why it's problematic**: All `PostAndReply` calls in `Node.fs` (e.g., `SubmitCommand`, `GetState`) are from external threads (the application or test code). Adding a call inside a handler module would hang the node permanently.

**How to avoid**: Never call `PostAndReply` from within a handler. If a handler needs to query state, use the `ctx.State` value already in scope. If a handler needs to send a message to itself, use `ctx.Inbox.Post` (fire-and-forget) instead. For external-facing APIs, always use `PostAndReply` from outside the agent.

**Current implementation**: `NodeMessage` cases that carry reply callbacks use `('T -> unit)` function types embedded directly in the DU cases (e.g., `ClientCommand of command: string * clientId: string option * seqNum: int64 option * (ClientCommandResult -> unit)`). The `RaftNode` public API methods (`SubmitCommand`, `LinearizableRead`, `AddPeer`, etc.) all use `PostAndReply` (or `PostAndAsyncReply`) from external threads, with the reply channel's `Reply` method passed as the callback. Handler modules in the Agent Layer (e.g., `NodeLocal.handleLocalMessage`) reply by calling the callback function directly without blocking.

---

## Timer Disposal Order on Shutdown

In the `Shutdown` handler, the election timer is disposed first, then the heartbeat timer, then `CancellationTokenSource.Cancel()`. The `CancellationTokenSource` registration on the TCP listener calls `listener.Stop()`.

**Why it's problematic**: If `CancellationTokenSource.Cancel()` were called before timer disposal, a timer callback could fire during shutdown and try to `Post` a message to a stopped/disposed `MailboxProcessor`. The current order prevents this.

**How to avoid**: When adding new resources that need cleanup in `Shutdown`, dispose timers and other resources **before** calling `Cancel()` on the `CancellationTokenSource`.

**Current implementation**: `NodeAgent.agentLoop` handles `Shutdown` by disposing `ctx.ElectionTimer` and `ctx.HeartbeatTimer` (both `System.Threading.Timer option`), then calling `ctx.CancellationTokenSource.Cancel()`. The `RaftNode.Dispose()` method posts `Shutdown` and waits for reply via `PostAndReply`, then disposes the `CancellationTokenSource` and agent.

---

## `initialLogIndex` vs `firstLogIndex`

Two sentinel values exist for log index arithmetic:

| Constant | Value | Meaning |
|---|---|---|
| `Log.initialLogIndex` | `0L` | "No entries" sentinel. `lastIndex` on empty log returns this. |
| `Log.firstLogIndex` | `1L` | First real log entry index. The no-op entry committed during `initLeaderState` is at index 1. |

**Why it's problematic**: Many functions use `initialLogIndex` as a special case (e.g., `isLogConsistent` checks `prevLogIndex = initialLogIndex` to skip consistency check). Mixing them up causes off-by-one errors — for example, using `initialLogIndex` where `firstLogIndex` is expected can cause incorrect conflict index calculations.

**How to avoid**: When checking "is this a valid real log index?", compare against `firstLogIndex`. When checking "is this the sentinel empty value?", compare against `initialLogIndex`. When computing `nextIndex - 1L`, the result may be `initialLogIndex` for a brand-new follower — that is correct and expected.

**Additional constants**: `Log.initialTerm = 0L` (initial term value), `Log.NoOpCommand = ""` (sentinel command for no-op entries and snapshot placeholders).

---

## `State.updateTerm` Clears Volatile State

When a higher term is discovered, `State.updateTerm` resets the node to `Follower`, clears `LeaderState` (set to `None`), empties `VotesReceived`, and sets `VotedFor = None`.

**Why it's problematic**: Calling `updateTerm` inadvertently (e.g., in the wrong branch of an RPC handler) can silently lose leader state, causing a legitimate leader to step down unnecessarily.

**How to avoid**: All RPC handlers use `updateTerm` as the first step when processing a message with a higher term. This is correct Raft behavior. However, custom code should never call `updateTerm` directly outside of `Election.handleRequestVote`, `Election.handleVoteResponse`, `Replication.handleAppendEntries`, or `Replication.handleAppendEntriesResponse` — they handle it internally.

---

## Snapshot Sentinel Entry Semantics

`Log.trim` inserts a sentinel entry at `lastIncludedIndex` with `NoOpCommand` and `ClientId = None`. This entry acts as a bookmark so that log index arithmetic (e.g., `lastIndex`, `termAt`) still works after log trimming.

**Why it's problematic**: The sentinel is an artificial entry that never existed in the original log. Code iterating over log entries (e.g., `applyCommitted`) must skip `NoOpCommand` entries — which `loopApplyCommitted` does via `entry.Command = Log.NoOpCommand -> loopApplyCommitted onApply state next`. Forgetting this check would attempt to apply the sentinel to the state machine.

**How to avoid**: Any new code that processes log entries should either skip `NoOpCommand` entries or handle the sentinel explicitly. The sentinel's `Index` is valid for lookups; its `Term` and `Command` should not be passed to the state machine callback.

---

## Config Change Leader Self-Removal

When `exitJointConsensus` processes a `FinalChange` that removes the current leader from the peer list, the leader automatically steps down to `Follower`.

**Why it's problematic**: After removal, the node no longer has voting rights. If tests or application code assume the node remains a leader after the config change completes, they will be surprised by the role transition.

**How to avoid**: When testing cluster membership changes, always check the role after `exitJointConsensus`. If the leader was removed, it becomes a Follower and a new leader will be elected among the remaining peers.

---

## `PendingReads` List Can Grow Unbounded

`PendingReads` is an ordinary F# list. Each `LinearizableRead` call on a Leader appends a new `PendingRead` entry. Entries are only removed when either:
- A quorum of followers respond and the read can be served (`ReadReady`), or
- The node ceases to be Leader (`ReadRedirect`).

**Why it's problematic**: If a leader is partitioned from its followers but the application continues to submit linearizable reads, the `PendingReads` list grows without bound, consuming memory.

**How to avoid**: This is a known limitation. In practice, the heartbeat timeout will eventually detect the partition and the node will step down (discovering a higher term), which flushes all pending reads with `ReadRedirect`. For long-lived partitions, consider adding a staleness check or maximum pending read limit.

---

## `Task<T>` / `ValueTask<T>` to `Async<T>` Conversion

.NET APIs returning `Task<T>` or `ValueTask<T>` require explicit conversion to F# `Async<T>` via `Async.AwaitTask`. In F# 10, many `ValueTask<T>` APIs are directly awaitable with `Async.AwaitTask` (e.g., `NetworkStream.ReadAsync`, `NetworkStream.WriteAsync`, `TcpListener.AcceptTcpClientAsync`). A few APIs still require `.AsTask() |> Async.AwaitTask` (e.g., `TcpClient.ConnectAsync` with `CancellationToken`).

**Why it's problematic**: Forgetting `.AsTask()` on APIs that need it causes a type error. Accidentally using the synchronous overload (e.g., `stream.Write` instead of `stream.WriteAsync`) bypasses async I/O.

**How to avoid**: Always use the `Async` suffix methods. Check the return type — most `ValueTask<T>` methods compile directly with `Async.AwaitTask` in F# 10; if you get a type error, add `.AsTask()`. Keep the pattern consistent with the existing code in `Transport.fs`.

---

## `createTimer` Captures `inbox.Post`

`NodeTimer.createTimer` stores a reference to `inbox.Post` in the timer callback. The timer fires by posting a message (`ElectionTimeout` or `HeartbeatTimeout`) to the inbox.

**Why it's problematic**: After `Dispose`, the timer may still fire if the finalizer runs later (rare but possible). More importantly, creating a new timer without disposing the old one leaks the old timer and its captured callback, keeping the `MailboxProcessor` reference alive.

**How to avoid**: The `applyElectionAction` / `applyHeartbeatAction` functions handle this correctly by reusing existing timers via `Timer.Change()`. Never create a `System.Threading.Timer` directly; always go through `NodeTimer`.

---

## `tryPromoteNonVotingPeers` Side Effects

`NodePromotion.tryPromoteNonVotingPeers` is an I/O wrapper that calls `promoteReadyNonVotingPeers` (pure state transformation), then `NodeUtil.saveIfChanged`, `NodeBroadcaster.broadcastAppendEntries`, `NodeApply.applyCommitted`, `NodeSnapshot.autoSnapshotIfNeeded`, and `NodePromotion.tryFinalizeConfiguration` — all within the handler's synchronous flow.

**Why it's problematic**: This function has significant side effects beyond simple state transformation. It's called from `NodeRaft.handleAppendEntriesResponse` and `NodeTimeout.receiveHeartbeatTimeout`. These side effects happen during message processing, which is correct but can make debugging non-deterministic behavior harder because promotion can cascade into further state changes including config finalization.

**How to avoid**: When debugging unexpected state transitions, consider that `tryPromoteNonVotingPeers` may be triggering configuration changes as a side effect of normal log replication responses. `tryFinalizeConfiguration` is also called independently in `handleRaftRPC` and `handleLocalMessage` for non-promotion-triggered config finalization.

---

## F# Structural Equality on `PersistentState`

`saveIfChanged` uses F#'s structural equality (`<>`) to compare the old and new `PersistentState`. This works correctly for F# records containing maps, options, and primitive types.

**Why it's problematic**: Structural equality on `Map<LogIndex, LogEntry>` is O(n) in the size of the map. On every message that returns a new `RaftState`, `saveIfChanged` compares the entire log, snapshot, and session table structurally. Most of the time, `PersistentState` is unchanged (only volatile state changed), making this an unnecessary O(n) comparison.

**How to avoid**: This is accepted as a trade-off for simplicity. For now, the comparison is cheap for typical log sizes in testing.
