# zurari (zrr)

Console dual-pane file manager, C#/.NET 8, with the entry point in `Program.cs`. Successor
to `../console-column-file-manager` (predecessor, superseded — don't edit for
new work); `../twf` is an earlier Terminal.Gui prototype with reference
JobManager/conflict-resolution/throttled-reload code worth checking before
reinventing.

## Build & test
- Always name the test project. Both green (0 warnings) before every commit:
  - `dotnet build Zurari.Tests/Zurari.Tests.csproj` — builds both projects.
  - `dotnet test Zurari.Tests/Zurari.Tests.csproj` — runs the suite.
- Never bare `dotnet build` / `dotnet test` from the repo root: there is no
  `.sln`, so `build` compiles only `Zurari.csproj` (warnings in test code go
  unseen) and `test` runs **zero** tests while still exiting 0 — a
  green-looking no-op that has been mistaken for a passing run.
- xunit tests in `Zurari.Tests/`; track red→green counts in commits (e.g.
  "green: 169/169") — established convention.
- `Console.WindowWidth`/`Height` throw with no console attached (xunit). Use
  `SafeWindowWidth()`/`SafeWindowHeight()` in anything that needs to be
  headless-testable.

## Never block the main loop
`Main()` is single-threaded: draw → idle work if no key pending → else read
key → await full handler chain. Anything awaited inside a key handler blocks
input AND drawing — `await next.LoadingTask` added in `907b4b7` was exactly
this bug and caused a real cursor-lag regression (see the Phase 9 plan in
`docs/superpowers/plans/`). Defer reads triggered by cursor movement to the
idle-tick + debounce pattern, never inline in a key handler. When wiring
cancellation, thread the real token (e.g. `column.ReadCts`) through every call
site — `CancellationToken.None` silently makes cancellation a no-op.

## Process
- Multi-step work is planned first in
  `docs/superpowers/plans/2026-07-06-dual-pane-phaseN-*.md` (Background,
  tasks, execution log with per-task evidence, Unresolved issues, completion
  checklist); design decisions go in `docs/superpowers/specs/*.md` first.
- Execution sessions record anything the plan didn't anticipate in
  "Unresolved issues" and stop, rather than deciding unilaterally.
- Phase branches: worktrees (`worktree-<name>` in `.claude/worktrees/<name>`),
  merged to main when the plan's checklist + manual verification are done.

## Design conventions
- Non-blocking UI: default to async + background job over a blocking modal.
- Prefer explicit user choices over inferring intent — e.g. a generic
  Esc/cancel inside a nested dialog should return to the previous choice
  point, not silently imply a specific outcome like "skip".
- User-facing strings are English (Phase 8 translated from Japanese);
  comments may stay Japanese.
