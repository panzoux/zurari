# Column resize + pane sync/swap — design

Date: 2026-08-23

## Problem

`ColumnWidth` is a `const int = 32` in `Program.cs`; every Miller column in both
panes is exactly that wide. Directories with long names truncate, shallow
directories with short names waste half the screen, and there is no way to trade
width between columns. In split mode there is also no way to make the two panes
agree on a path, or to make them trade places, without navigating each by hand.

## Feature set

| Key | Action |
|---|---|
| `[` / `]` | Narrow / widen the column under the cursor |
| `Shift+Left` / `Shift+Right` | Same, where the terminal passes the key through |
| `Shift+Y` | Copy the focused pane's column widths onto the other pane |
| `o` | Set the focused pane's path to the other pane's path |
| `Shift+O` | Swap the top and bottom panes |

## Decisions

**Resize target is the column under the cursor**, not the whole pane. Each
column carries its own width, so a directory with long names can be widened
without stretching its neighbours.

**Width lives on `Column` and is carried over by path.** Columns to the right of
the cursor are destroyed and recreated by `RebuildRightSideAsync` on *every*
cursor move, so a width stored only on the instance would evaporate immediately.
A per-pane `path → width` memory (an LRU pair mirroring the existing
`_cursorMemory` / `_cursorMemoryKeys`) restores the width whenever a column for
that path is rebuilt.

**Width memory is per pane, not global.** Sharing one map would force both panes
to the same width whenever they showed the same directory, which is exactly what
`Shift+Y` exists to opt into. Independence is the default; syncing is explicit.

**Session only.** No new config file, no persistence across restarts. Widths are
cheap to re-set and a settings file is a separate concern from this feature.

**`[` / `]` are the primary resize keys.** Shift+Arrow was the original binding,
but Windows Terminal binds Shift+Arrow to "extend text selection" and consumes
it before the application ever sees it — the app reads keys with a plain
`Console.ReadKey(true)` and has no way to intercept that. The Shift+Arrow
binding is kept as a fallback for terminals that do pass it through. The bracket
handlers deliberately ignore modifiers: on some keyboard layouts `[` and `]`
themselves require Shift or AltGr, so a matching `KeyChar` already settles
intent.

**Step ±2, min 10, max = terminal width.** ±2 is fast enough to be usable while
still landing on an exact width. 10 keeps the 2-char prefix, the separator and a
readable name fragment.

**After `Shift+O` the cursor stays on the same side.** Top stays top: the swap
changes *which content is active*, matching Total Commander's `Ctrl+U`. Column
widths ride along with the panes for free, since `Width` lives on `Column`.

**`o` reuses `JumpToPathAsync`.** That function already cancels in-flight reads,
rebuilds the column chain, restores cursor memory and fixes horizontal scroll.
It awaits each column's `LoadingTask` and therefore blocks the main loop for the
duration of the walk — exactly as the existing bookmark (`b`) and history (`h`)
jumps do. The "never block the main loop" rule targets reads triggered by
*cursor movement*; an explicit jump is a different case, and reusing the one
code path beats inventing a non-blocking variant for this key alone.

## Layout consequences

With variable widths, three fixed-width computations stop being valid and are
rewritten as walks over the actual column widths:

- the draw loop in `DrawPaneToLines`,
- `UpdateHorizontalScroll`'s `SafeWindowWidth() / ColumnWidth` visible-column
  count,
- `StartPreviewLoad`'s `drawnCols * ColumnWidth` preview sizing.

The renderer and the preview sizer share one helper (`ComputeDrawnColumnsWidth`)
so the drawn width and the rasterized preview width cannot drift.

The preview pane is not a `Column` and has no width of its own; it keeps
`DefaultColumnWidth` (32) as its reserve and minimum. Resizing the preview is
out of scope.

Terminal shrink is handled in `HandleResizeIfNeeded`, which clamps every
column's width in *every* pane to the new terminal width. The draw loop also
clamps the first visible column to the space remaining, as a safety net that
guarantees a pane never renders blank.

## Out of scope

- Persisting widths across restarts.
- Resizing the preview pane.
- Updating the status-line hint text — it is already near the width limit.
