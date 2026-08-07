# Create Directory Dialog — Design Spec

**Date**: 2026-08-07
**Status**: Design approved, ready for implementation planning

---

## Overview

Add a "New Folder" dialog bound to `Shift+K`, letting the user create a subdirectory
in the currently active column's directory without leaving the keyboard. It reuses
the existing dialog infrastructure (`TextInputDialogState` / `ShowTextInput`, the
same component Rename already uses) rather than inventing a new dialog kind.

While researching Rename's implementation as a reference, two pre-existing gaps
were found that this design also fixes, since they directly affect the new
dialog's stated requirements:

1. `TextInputDialogState` rendering truncates at screen width when the buffer is
   long, which can silently drop the cursor marker off-screen.
2. Rename (and, by the same mechanism, Create Directory if left unaddressed)
   only refreshes the pane where the action happened — a second pane showing the
   same directory goes stale until the user navigates away and back.

Both fixes are small, reuse existing patterns, and benefit Rename as well as
Create Directory — see "Shared dialog fixes" and "Cross-pane refresh" below.

---

## Trigger & Guard

- `Shift+K` — added as a new `case ConsoleKey.K:` in the same top-level switch
  that already handles `Shift+V` (preview toggle, `Program.cs` around line 3179),
  following that exact pattern:
  ```csharp
  case ConsoleKey.K:
      if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
          RequestCreateDirectory(pane);
      break;
  ```
- No explicit mode guard is needed: the main loop already routes keys as
  `Dialog > Leap > Search > normal` (`Program.cs` ~2893-2910), so this case is
  structurally unreachable while a dialog is open or Leap/Search is active —
  same as every other single-letter shortcut (`d`, `r`, `h`, ...).
- On the `::DRIVES::` pseudo-column, `RequestCreateDirectory` silently no-ops —
  same convention `SelectedIsDeletable` already establishes for Rename/Delete.

---

## Dialog UX

Reuses `ShowTextInput("New Folder", "", onResult)`. Hint line:
`[Enter]Create  [Esc]Cancel` (same style as Rename's `[Enter]Confirm [Esc]Cancel`,
existing convention — no OK/Cancel buttons).

### Editing keys

Home/End/Backspace/Delete/Left/Right/character-insert already work via the
existing `HandleTextInputDialogKey` / `TextInputBuffer` machinery. Two additions,
made generically on `TextInputDialogState` (so Rename benefits too):

- **Ctrl+U**: clear the buffer (`dialog.Buffer = ""; dialog.CursorPos = 0;`).
  Currently missing entirely.
- **Paste**: no new code needed. The console feeds a clipboard paste to the app
  as a rapid sequence of individual `ConsoleKeyInfo` events, each of which
  already flows through the same per-character insert path used for typing.

### Character filtering

Add an optional field to `TextInputDialogState`:

```csharp
public Func<char, bool>? CharFilter; // null = unrestricted (existing behavior)
```

`HandleTextInputDialogKey` checks `dialog.CharFilter == null || dialog.CharFilter(key.KeyChar)`
before inserting a character. Defaulting to `null` keeps Rename's behavior
byte-identical; only Create Directory sets a filter.

Create Directory's filter rejects, as-you-type (per user decision: block rather
than validate-on-submit):

- Windows (`IsWindows()`): `< > : " / \ | ? *` and ASCII control characters.
- Non-Windows: only `/` and NUL — the only characters actually invalid in a
  filename there. (Flagged during brainstorming as a real behavior branch, not
  just style — approved as-is; the app is Windows-first but already has
  `IsWindows()`-gated branches elsewhere, e.g. `RebuildRightSideAsync`'s path
  comparer.)

### Reserved names (checked on Enter, not per-keystroke)

Per-keystroke filtering can't catch these — they depend on the whole name:

- Reserved device names: `CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`, `LPT1`-`LPT9`
  (case-insensitive, with or without an extension — e.g. `NUL.txt` is also reserved).
- Trailing dot or trailing space (Windows silently strips/rejects these).

Windows-only checks, matching the same `IsWindows()` gating as the character filter.

### Cursor visibility & horizontal scrolling

**Bug found during research**: `BuildDialogContentLines`'s `TextInputDialogState`
case builds the display line as `beforeCursor + "\x1b[7m█\x1b[0m" + afterCursor`,
then `ComposeDialogIntoFrame` sizes the dialog to fit that whole string, capped
only by screen width. When the buffer is wide enough to hit that cap,
`FitRowToWidth` truncates using `CharacterWidth.SmartTruncate` on the
**ANSI-stripped plain text** — which discards the inverse-video cursor marker
entirely if the truncation point falls past it. This is exactly the situation
the "long name / must show cursor" requirement calls out.

**Fix**: cap the input field at a fixed width (50 display columns — comfortably
narrower than a typical terminal, generous for folder names) and scroll the
visible window so the cursor is always inside it, using
`TextInputBuffer.DisplayColumnAtCursor` (currently defined but unused — this is
its intended purpose). When the buffer's display width exceeds 50 columns, the
visible slice is computed by column range (not character index, so CJK
double-width characters aren't split), keeping the cursor within, e.g., a
5-column margin of either edge before scrolling further. This changes the
`TextInputDialogState` branch of `BuildDialogContentLines` only — Confirm,
ListSelect, and JobList dialogs are untouched.

This fix applies to the shared dialog component, so Rename also gets correct
cursor display and scrolling for long filenames, not just Create Directory.

### Centering within the active pane (split mode)

Today `DrawDialogOverlay(frame, width, height)` is called with the full screen
dimensions, so `CalcDialogRect` always centers on the whole screen — including
when `State.SplitActive` is true, which looks off-center relative to the pane
the user is actually looking at.

**Fix**: `DrawDialogOverlay` computes the focused pane's Y-range before calling
`ComposeDialogIntoFrame`:

```csharp
static void DrawDialogOverlay(string[] frame, int width, int height)
{
    if (State.Dialog == null) return;

    int dialogY = 0, dialogHeight = height;
    if (State.SplitActive)
    {
        (int topHeight, int bottomHeight) = ComputeSplitPaneHeights(height);
        if (State.FocusedPane == 0) { dialogY = 0; dialogHeight = topHeight; }
        else { dialogY = topHeight + 1; dialogHeight = bottomHeight; }
    }

    ComposeDialogIntoFrame(frame, State.Dialog, width, dialogHeight, yOffset: dialogY);
}
```

`DialogLayout.CalcDialogRect` itself is unchanged — it's called with
`dialogHeight` (the pane's sub-rectangle height) in place of the full screen
height, so it still centers correctly within that local space. The new
`yOffset` parameter lives on `ComposeDialogIntoFrame`: every row it writes via
`OverwriteFrameRow` (top border, content rows, bottom border) gets `yOffset`
added to its absolute frame Y. X centering is unaffected — panes are stacked
top/bottom, not side-by-side, so horizontal centering across the full width is
already correct in both modes. `ComputeSplitPaneHeights` is already a pure
function used elsewhere for the same calculation, so this recomputation is
cheap and doesn't need new state threaded through.

---

## Creation Logic

New `RequestCreateDirectory(Pane pane)` / `ExecuteCreateDirectoryAsync(...)`,
mirroring the shape of `RequestRenameSelected` / `ExecuteRenameAsync`:

```
RequestCreateDirectory(pane):
    col = pane.Columns[pane.ActiveColumn]
    if col.Path == "::DRIVES::": return   // silent no-op, same as Rename/Delete guard

    ShowTextInput("New Folder", "", CharFilter: MakeDirNameCharFilter(),
        onResult: async name => await ExecuteCreateDirectoryAsync(pane, col, name))
```

```
ExecuteCreateDirectoryAsync(pane, col, nameResult):
    if nameResult == null: return                       // Esc
    name = nameResult.Trim()
    if name.Length == 0: return                          // silent cancel — same precedent as Rename's empty-name handling

    if IsReservedName(name) or EndsWithDotOrSpace(name):  // Windows-only check
        _lastErrorMessage = $"\"{name}\" is not a valid directory name"
        return

    if col.Entries has an entry matching name (case-insensitive, comparing
       against entry text with any trailing "/" stripped):
        _lastErrorMessage = $"\"{name}\" already exists"
        return

    fullPath = BuildFullPath(col.Path, name)
    try:
        Directory.CreateDirectory(fullPath)
        OperationLog.Append(new OpLogEntry(..., "MKDIR", "OK", fullPath, fullPath, ""))
        _lastErrorMessage = null

        refreshed = await CreateColumnAsync(col.Path)
        if refreshed.LoadingTask != null: await refreshed.LoadingTask
        refreshed.RestoreTo = name + "/"
        SeekRestoreTo(refreshed)
        refreshed.Marked = col.Marked
        MarkHelper.ReconcileAfterReload(refreshed)
        pane.Columns[pane.ActiveColumn] = refreshed
        await RebuildRightSideAsync(pane, pane.ActiveColumn)   // shows the new (empty) dir's child column "for free"

        await RefreshOtherPanesAsync(pane, col.Path)           // see Cross-pane refresh, below

    catch (Exception ex):
        OperationLog.Append(new OpLogEntry(..., "MKDIR", "FAIL", fullPath, fullPath, ex.Message))
        _lastErrorMessage = $"Create failed: {ex.Message}"
        await RefreshCurrentAsync(pane)   // same fallback ExecuteRenameAsync uses on failure
```

Selecting the new folder and calling `RebuildRightSideAsync` means its child
column (empty listing) appears automatically — this is exactly the mechanism
Miller-column navigation already uses whenever the cursor lands on a directory,
so no extra code is needed for that part.

Collision checking is done against the already-loaded `col.Entries` in memory
(no extra disk I/O), case-insensitively (Windows filesystems are
case-insensitive) — matches how the rest of the navigation code already treats
directory listings as the source of truth between refreshes.

---

## Cross-pane refresh (applies to Rename too)

**Gap found during brainstorming**: cross-pane refresh currently only exists for
background jobs. `RefreshAffectedColumnsAsync(job)` (`Program.cs` ~3517) walks
`State.Panes`, and for any pane whose columns show an affected directory,
either refreshes it immediately or — if that pane is mid-Leap/Search — defers
via `pane.NeedsRefresh = true` (consumed later, `Program.cs` ~4513/4556). This
is only ever called from the job-progress-tick and job-completion paths
(`Program.cs` ~2837, ~3480). Rename is a synchronous one-shot operation and
never calls it — so today, if pane B is browsing the same directory where the
user renames something in pane A, pane B silently goes stale until the user
navigates away and back.

Left unaddressed, Create Directory would have the identical gap: create a
folder in pane A, and pane B (already looking at that same directory) wouldn't
show it. Per user decision, this is fixed for both — a new shared helper,
modeled directly on the existing job-refresh logic:

```csharp
static async Task RefreshOtherPanesAsync(Pane actingPane, string affectedDir)
{
    var affectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { affectedDir };
    foreach (Pane pane in State.Panes)
    {
        if (ReferenceEquals(pane, actingPane)) continue;
        if (!pane.Columns.Exists(c => affectedDirs.Contains(c.Path))) continue;

        if (pane.Leap.Active || pane.Search.Active) { pane.NeedsRefresh = true; continue; }
        await RefreshPaneNowAsync(pane, affectedDirs);
    }
}
```

`actingPane` is skipped deliberately — that pane's active column was already
replaced directly (with its own `RestoreTo`-driven cursor placement), and
running it through the generic `RefreshPaneNowAsync` path afterward would
clobber that placement for no benefit.

Wired in at two call sites:

- `ExecuteCreateDirectoryAsync`, on success: `await RefreshOtherPanesAsync(pane, col.Path)`
  — `col.Path` is the directory the new folder was created *into*.
- `ExecuteRenameAsync`, on success (new line, added next to the existing
  `RebuildRightSideAsync(pane, pane.ActiveColumn)` call there): `await RefreshOtherPanesAsync(pane, col.Path)`
  — `col.Path` is the directory containing the renamed entry.

In single-pane mode (`State.Panes.Count == 1`) the loop body never matches
anything but `actingPane`, which is always skipped, so this is a no-op there —
no mode branching needed.

---

## Testing

### Unit tests (`Zurari.Tests/`, following `TextInputBufferTests.cs` /
`DialogLayoutTests.cs` conventions — pure functions, no `Console` dependency)

- Character filter: accepts ordinary/CJK characters, rejects each invalid
  character in the Windows set, rejects only `/` and NUL on the non-Windows
  path.
- Reserved-name detection: `CON`, `NUL.txt`, `com1` (case-insensitive), trailing
  dot, trailing space — all rejected; ordinary names accepted.
- Collision detection: case-insensitive match against a stubbed entries list,
  including matching a directory entry that has a trailing `/`.
- Scroll/display-line builder: cursor at start/middle/end of a buffer wider
  than the 50-column cap scrolls correctly and always includes the cursor
  marker in the visible slice; buffers narrower than the cap are unaffected
  (regression coverage for existing short-name behavior, e.g. Rename).
- `ComposeDialogIntoFrame`'s new `yOffset` parameter: dialog rows land within
  the given sub-rectangle instead of always starting at the top of the screen.
- `RefreshOtherPanesAsync`-equivalent logic: given a stubbed pane list, only
  panes (other than the acting one) whose columns match the affected directory
  are refreshed; a matching pane in Leap/Search mode gets `NeedsRefresh = true`
  instead of an immediate refresh.

### Manual verification (via the `run` skill)

- 1-pane mode: create a folder, confirm it's created, selected, and its (empty)
  child column appears.
- 2-pane (upper/lower) mode: dialog centers within the focused pane; create a
  folder while the other pane shows the same directory, confirm it updates
  without manual navigation.
- Long name: type or paste a name wider than 50 columns, confirm the cursor
  stays visible and the field scrolls correctly in both directions.
- Japanese IME input and paste from clipboard.
- Ctrl+U clears the buffer.
- Invalid character typed/pasted is silently rejected.
- Reserved name (e.g. `CON`) and trailing-space name are rejected on Enter with
  a status-bar error.
- Name collision with an existing entry shows the "already exists" error.
- Rename, re-verified after the shared fixes: cursor/scrolling still correct
  for long filenames; renaming in one pane updates the other pane if it's
  showing the same directory.
