# Plan: column resize + pane sync/swap

Design: `docs/superpowers/specs/2026-08-23-column-resize-design.md`
Branch: `worktree-column-resize` (`.claude/worktrees/column-resize`)
Baseline: build 0 warnings, tests green 230/230.

## Background

Every Miller column is a fixed 32 cells wide (`const int ColumnWidth`). This
adds per-column resize (`Shift+Left`/`Shift+Right`), width sync between panes
(`Shift+Y`), path sync (`o`) and pane swap (`Shift+O`). See the design doc for
the decisions and their rationale.

## Tasks

1. **Width state** — `DefaultColumnWidth`/`MinColumnWidth`/`ColumnWidthStep`
   consts; `Column.Width`; per-pane `WidthMemory` LRU + `SaveColumnWidth` /
   `ApplyColumnWidth` helpers.
2. **Carry-over wiring** — save/apply at `RebuildRightSideAsync`,
   `CancelAllReadsInPane`, `JumpToPathAsync`, and the four in-place column
   replacement sites.
3. **Width-aware layout** — `ComputeDrawnColumnsWidth` helper; variable-step
   draw loop in `DrawPaneToLines`; `widthOverride` in `DrawColumnToLines`;
   rewritten `UpdateHorizontalScroll`; `StartPreviewLoad` sizing;
   `HandleResizeIfNeeded` width clamp across all panes.
4. **Key bindings** — `ResizeActiveColumn`, `SyncColumnWidthsToOtherPane`,
   `SyncPaneToOtherPathAsync`, `SwapPanes`, wired into `HandleSpecialKeyAsync`.
5. **Tests** — `Zurari.Tests/ColumnWidthTests.cs` plus pane swap/sync cases in
   `DualPaneTests.cs`.
6. **Verify** — `dotnet build` (repo root) and `dotnet test` (from
   `Zurari.Tests/`), both green, 0 warnings.

## Execution log

Baseline before any change: `dotnet build` 0 warnings / 0 errors;
`dotnet test` (from `Zurari.Tests/`) green 230/230.

1. **Width state** — done. `DefaultColumnWidth` / `MinColumnWidth` /
   `ColumnWidthStep` consts, `Column.Width`, per-pane `WidthMemory` +
   `WidthMemoryKeys`, `SaveColumnWidth` / `ApplyColumnWidth`. Save at the
   default width *clears* the entry, otherwise an old width would come back
   after the user reset a column.
2. **Carry-over wiring** — done. Saves in both right-side pop loops
   (`RebuildRightSideAsync`, the flicker-avoiding prune), `CancelAllReadsInPane`
   and `JumpToPathAsync`; applies after every `Columns.Add`; direct
   `refreshed.Width = current.Width` at the four in-place replacement sites.
3. **Width-aware layout** — done. `NextColumnWidth` holds the single allocation
   rule (fit → desired; overflow → 0, except the first visible column which is
   clamped to what remains); `ComputeDrawnColumnsWidth` builds on it and is used
   by both the draw loop and `StartPreviewLoad`, so drawn width and rasterized
   preview width cannot drift. `UpdateHorizontalScroll` rewritten as a
   right-to-left budget walk. `DrawColumnToLines` took a trailing optional
   `widthOverride` so the existing test call sites still compile.
   `HandleResizeIfNeeded` clamps widths across all panes.
   Build after this task: 0 warnings, tests still 230/230.
4. **Key bindings** — done. `ResizeActiveColumn`, `SyncColumnWidthsToOtherPane`,
   `SwapPanes`, `SyncPaneToOtherPathAsync`, wired to Shift+←/→, Shift+Y, `o`,
   Shift+O. No `await` in the resize path. Build 0 warnings, tests 230/230.
5. **Tests** — `Zurari.Tests/ColumnWidthTests.cs` (24 cases) and
   `Zurari.Tests/PaneSyncSwapTests.cs` (14 cases, in the `GlobalStateTests`
   collection). Two of my own layout expectations were wrong on first run and
   were corrected against the code's actual (pre-existing, deliberate)
   behaviour: `UpdateHorizontalScroll` never *lowers* an existing scroll
   position (Finder-style preservation), and the right-hand column reserve is
   subtracted from the budget before the leftward walk.
6. **Verification** — `dotnet build` 0 warnings / 0 errors;
   `dotnet test` green **267/267** (230 → 267, +37 new).

### Bug found and fixed en route

`JumpToPathAsync` was broken on Windows for every non-drive-root path. The root
column is the drive list (`::DRIVES::`), but the code selected the drive entry
inside it and then walked the path segments starting from *that same column* —
so the first segment (`Users`, …) was looked up among drive names, never
matched, and the function bailed out with "Path not found" leaving the pane
showing only the drive list. This also broke the existing bookmark (`b`) and
history (`h`) jumps, not just the new `o`.

Fix: after selecting the drive, push the drive-root column (`C:\`) and advance
`ActiveColumn` before the segment loop. Guarded by `IsWindows()`, so the POSIX
path (root column is `/`) is untouched. Covered by
`SyncPaneToOtherPath_BuildsTheWholeColumnChainFromTheDriveList`, which asserts
the full chain `::DRIVES::` → `C:\` → …segments… → target.

## Unresolved issues

- **Shift+Arrow never reaches the app** — confirmed by the user on Windows, not
  just a POSIX concern. Windows Terminal binds Shift+Arrow to "extend text
  selection" and swallows it; the app reads keys with a plain
  `Console.ReadKey(true)` and cannot intercept a terminal-level binding. Some
  Linux/macOS terminals send `ESC [1;2D`, which .NET may surface without the
  modifier, for the same net effect. Resolved by adding `[` / `]` as the primary
  resize keys; the Shift+Arrow binding stays as a fallback for terminals that do
  pass it through. Not worth further work unless the app moves off
  `Console.ReadKey`.

### Bugs found during the user's manual verification (all pre-existing on `main`)

Verified against `main` with `git show main:Program.cs` — none is a regression
from this branch.

**1. Move went to the wrong directory.** `StartCopyOrMoveAsync` took the
destination from `otherPane.Columns[otherPane.ActiveColumn].Path`, but the pane's
own path line displays `GetCurrentFullPath`, i.e. the directory *under the
cursor*. With the other pane's cursor on `1fol/` inside `D:	est`, the UI
promised `D:	estfol` and the code used `D:	est`. Extracted
`ResolvePaneDestination`, which prefers the selected directory and falls back to
the column path when the cursor is on a file.

**2. That wrong destination silently destroyed the directory.** Moving
`D:	estfol2` into `D:	est` makes `dest == src`.
`CopyOrMoveDirectoryAsync` then creates the destination (already there), moves
each child onto itself, finds the source "empty" and runs
`Directory.Delete(srcDir)` — the folder vanishes. Guarded twice: in
`EnqueueCopyOrMoveJob` with a user-facing message, and inside `RunBatchAsync`
so no caller can trigger the deletion. Both use the existing
`PathOverlapHelper`, extended with a fail-closed `PathsOverlapSafe`.
Confirmed the guard is load-bearing by disabling it and watching
`MoveAsync_EmptyDirectoryIntoItsOwnParent_DoesNotDeleteIt` fail with
"the directory must survive a move onto itself".

**3. The "Collection was modified" crash.** Dialog result callbacks were typed
`Action<T>`, so the six `async` lambdas passed to `ShowConfirm`/`ShowTextInput`/
`ShowListSelect` were **async void**. Each ran synchronously only up to its first
real `await` (e.g. `await refreshed.LoadingTask` in
`ExecuteCreateDirectoryAsync`); the remainder — including
`pane.Columns[pane.ActiveColumn] = refreshed` and `RebuildRightSideAsync` —
resumed on a thread-pool thread and raced the main loop, which was concurrently
enumerating `pane.Columns` in `ExtendRightSideIfReadyAsync`. Changed `OnResult`
to `Func<T, Task>`, made the close helpers and dialog key handlers async, and
awaited them from `HandleDialogKeyAsync`, so callbacks now complete on the main
loop before it continues — matching the "Main() awaits the full handler chain"
rule in CLAUDE.md. The `Action<T>` overloads of `Show*` remain, so the eight
synchronous call sites are unchanged.

### History and bookmark defects (second verification round, all pre-existing)

**History kept dead entries.** Selecting a history entry whose directory was
gone failed and left the entry in place, so it failed again next time.
`JumpToPathAsync` now returns success, and the history callback prunes on
failure. Bookmarks deliberately do not get this: history accumulates
automatically and a dead entry is worthless, while a bookmark is data the user
created on purpose.

**Renames orphaned history and bookmarks.** Both now follow a renamed directory
(and everything under it) through a shared `PathRewriteHelper` that matches only
on separator boundaries, so renaming `old` never captures `oldish`. Duplicates
created by the rewrite collapse.

**`Shift+B` bookmarked the parent.** It used `Columns[ActiveColumn].Path` — the
same defect class as the copy/move destination — so it now shares
`ResolvePaneDirectory` (renamed from `ResolvePaneDestination`, since bookmarks
use it too).

**Selected dialog rows lost their highlight when truncated.** `FitRowToWidth`
stripped ANSI before calling `SmartTruncate`, so short bookmark rows inverted and
long paths did not. Truncation is now ANSI-aware: it keeps the 2/3–1/3
middle-ellipsis rule and re-emits the escapes of dropped cells, so SGR state
stays correct across the ellipsis. Confirmed load-bearing by reverting it and
watching `SelectedRowLongerThanWidth_KeepsItsHighlight` fail.

**History recording was unpredictable.** Only Enter, Backspace and explicit
jumps recorded — never arrow Left/Right, which is how you actually move in a
Miller-columns browser. Now `MoveLeft`/`MoveRightAsync` record the directory
they land on. Cursor up/down deliberately records nothing, so browsing a folder
does not flood the list (user's choice). `::DRIVES::` is never recorded: it is
not a real path and jumping to it always fails. Most-recent-first dedup is
unchanged.

## Completion checklist

- [x] Build 0 warnings, tests green — 230/230 → **316/316**
- [ ] Manual verification checklist run by the user (TUI cannot be driven here)
- [ ] Merge to `main`, remove worktree

## Manual verification checklist (for the user)

1. `]` several times on a column with long filenames — only that column widens,
   2 cells per press, names stop truncating; neighbours unchanged.
2. `[` past the floor — stops at 10, no corruption or negative padding.
   (`Shift+→` / `Shift+←` do the same where the terminal passes them through.)
3. Widen a column, `→` into a child, `←` back, move the cursor up and down,
   `→` again — the widened column returns at its set width (carry-over by path).
4. `Ctrl+W` to split. Resize in the top pane — the bottom pane is untouched.
   `Shift+Y` — the bottom pane adopts the top's widths.
5. `Shift+V` preview on next to a wide column — the preview still gets the
   remaining width and rasterizes at the right size.
6. Point the two panes at different directories, press `o` — the focused pane
   jumps to the other's path. Also re-check `b` (bookmark) and `h` (history)
   jumps, which the `JumpToPathAsync` fix above also repairs.
7. `Shift+O` — top and bottom contents trade places, the cursor stays on the
   same physical side, both previews redraw at their new heights.
8. Shrink the terminal below the widest column — columns clamp, nothing renders
   past the right edge, no leftover ANSI garbage.
