# AGENTS.md

This file provides guidance for AI agents working in this repository.

## AGENTS.md Editing Rules

- **Don't write what's in the codebase** — information that can be obtained by reading source code or project files must not be written in AGENTS.md.
- **Don't duplicate README.md** — content already described in README.md should only be referenced by a link (`See [README.md](...)`).

### Documentation Location Rules

| Topic | Destination |
|---|---|
| Architecture and design discussions | `docs/architecture.md` |
| Design trade-offs | `docs/trade-off.md` |
| Common mistakes / gotchas | `docs/gotchas.md` |

- Only keep project-specific implicit rules in AGENTS.md. The topics above belong in their corresponding `docs/*.md` files.
- When a new trade-off or gotcha arises, first consider appending to the relevant `docs/` file. Only add to AGENTS.md if it's an "implicit rule not obvious from the codebase."

---

## Architecture

See [docs/architecture.md](docs/architecture.md).

---

## Design Trade-offs

See [docs/trade-off.md](docs/trade-off.md).

---

## Recurring Gotchas

See [docs/gotchas.md](docs/gotchas.md).

---

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

## Coding Conventions

- Prefer functional programming idioms over imperative ones throughout the codebase — including test code.
- **Favor expressions over statements** — Use `match` expressions, `if`/`then`/`else`, and pattern matching instead of imperative control flow.
- **Leverage discriminated unions** — Model domain concepts with DUs for exhaustiveness checking.
- **Use `[<TailCall>]` on recursive functions** that loop to prevent stack overflows.
- Do not introduce new external NuGet packages without checking existing dependencies in the `.fsproj` files first.
- **Cyclomatic complexity** — Every function/method must keep its Coverlet complexity ≤ 15 (hard limit). Keep it ≤ 10 where practical. The `scripts/check-complexity.fsx` script checks this automatically from `coverage.cobertura.xml` after `dotnet test`. See `Directory.Build.props` for threshold configuration. If the check fails, split the function into smaller helpers or simplify branching.

## Testing Conventions

- After any code change, run `dotnet test` and confirm **all tests pass**.
- The `dotnet test` output includes a **Cyclomatic Complexity Report** (from coverage data). Check that no function exceeds complexity 15 (error threshold). Warnings above 10 should be addressed where practical.
- Maintain high unit test coverage (target: ≥ 80% line coverage).If line coverage falls below 80%, add test code to restore it above the threshold before merging.
- **Test ordering rules**:
  1. Within each test file, `[<Fact>]` functions must appear in the same order as the corresponding functions/methods/constructors in the source file under test.
  2. When multiple test cases target the same source function, order them by **test priority**: normal (happy path) → error cases → fault/failure scenarios.
  3. **Prefer data-driven tests** (`[<Theory>]` + `[<InlineData>]`) when multiple test cases share the same test logic but differ only in inputs or expected outputs. This reduces code duplication and makes it easy to add new cases.
- **Use a unique suffix** per test — tests may run in parallel.

## Test Suite

**`IntegrationTests.fs`** exercises end-to-end Raft scenarios (leader election, log replication, log inconsistency recovery, split-brain, stale leader rejection) by calling the pure `Election`, `Replication`, and `State` module functions directly — **no TCP sockets, no `RaftNode` actor, no real timers**. These tests are fast and deterministic.

**`TransportTests.fs`** is the only test file that opens real TCP sockets on loopback. It may conflict if ports are already in use; run in isolation when needed.
