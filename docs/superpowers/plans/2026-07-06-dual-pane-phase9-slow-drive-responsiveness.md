# Phase 9: Slow-Drive Responsiveness + Lower-Pane Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cursor movement stays instant on slow drives (cloud/network) by never blocking the input loop on directory reads; directory reads pause during key repeat and resume 300ms after the cursor stops. Also fix Shift+V preview not rendering in the lower split pane.

**Architecture:** The regression (commit `907b4b7`) added `await LoadingTask` on the per-keystroke path, defeating the async design from `97496a2`. Fix: (1) make `RebuildRightSideAsync` non-blocking (never await `LoadingTask`), (2) move per-keystroke right-side rebuilds to the main loop's idle branch behind the existing 300ms debounce (same pattern Leap/Search already use at Program.cs:2843-2862), (3) fix broken read cancellation (loader must observe `column.ReadCts`, which `CancelRightSideReads` cancels — today it observes a caller token that is always `CancellationToken.None`). No new spinner needed: `UiActivityInProgress` (Program.cs:5727) already includes `AnyColumnLoading`, so the existing `[loading N]` header counter animates once the loop stops blocking.

**Tech Stack:** C# / .NET console app (single file `Program.cs`), xunit tests in `Zurari.Tests`. Windows-first.

**Verification commands:** `dotnet build` and `dotnet test` from repo root. Both must pass before each commit.

**Line numbers below are anchors as of commit `ffb7a43`** — they shift as tasks land. Locate by the quoted code, not the number.

---

## Background (read before starting)

- Main loop is single-threaded: `Main()` at Program.cs:2784. One iteration = maybe-draw → if no key available, run idle work + sleep 50ms → else `Console.ReadKey` → await the full handler chain. Anything awaited in a key handler blocks input AND drawing.
- `CreateColumnAsync(path, ct)` (Program.cs:3599) returns a `Column` immediately with `column.LoadingTask` running on the thread pool filling `column.Entries`. `column.IsLoading` is true until done; the loader's `finally` calls `_redrawGate.Request()`.
- The bug: `RebuildRightSideAsync` (Program.cs:5385) does `await next.LoadingTask` (5400-5401), and it is awaited from MoveUp/MoveDown/PageUp/etc. So every cursor move onto a new directory blocks until the read completes.
- Cancellation bug: `CreateColumnAsync` creates `column.ReadCts` (3634) but the loader observes the `ct` **parameter**; all 7 call sites pass `CancellationToken.None`. `CancelRightSideReads` (3898) cancels `ReadCts` — a no-op today. On slow drives, stale reads keep running and compete for I/O.
- Existing idle-branch debounce pattern to copy: Leap recompute at Program.cs:2843-2852 (flag + last-input-time + `NavigationDebounceMs` check inside `if (!Console.KeyAvailable)`).

**Out of scope for Phase 9** (record, don't fix):
- `JumpToPathAsync` (Program.cs:5465) still awaits each segment's `LoadingTask` — it genuinely needs entries to find the next segment, and it's a discrete user action (bookmark/history jump), not key repeat. Accepted limitation; candidate for Phase 10.
- Dead code `GetRealPath` (3777) and `FindDeepestAccessiblePath` (3803) — unused remnants; separate cleanup.

---

### Task 1: Console-safe size helpers (unblocks headless tests)

`Console.WindowWidth`/`WindowHeight` throw `IOException` when no console is attached (xunit). The loader task (3641) and `UpdateHorizontalScroll` (3987) hit this, so none of the code under change is currently testable headless.

**Files:**
- Modify: `Program.cs` (inside `class Program`, near the top with other static helpers)
- Test: `Zurari.Tests/AsyncColumnLoadTests.cs` (new file)

- [x] **Step 1: Write the failing test**

Create `Zurari.Tests/AsyncColumnLoadTests.cs`:

```csharp
using Xunit;

public class AsyncColumnLoadTests
{
    internal static string MakeTempDir(params string[] fileNames)
    {
        string dir = Path.Combine(Path.GetTempPath(), "zrr_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in fileNames)
            File.WriteAllText(Path.Combine(dir, f), "");
        return dir;
    }

    [Fact]
    public async Task CreateColumnAsync_Headless_CompletesWithoutConsole()
    {
        string dir = MakeTempDir("a.txt", "b.txt");
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            Assert.NotNull(col.LoadingTask);
            await col.LoadingTask!;
            Assert.False(col.IsLoading);
            Assert.Equal(2, col.Entries.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

Note: this test already uses the Task-2 signature `CreateColumnAsync(dir)` (no ct parameter). Until Task 2 lands, write it as `CreateColumnAsync(dir, CancellationToken.None)` and update in Task 2. `CreateColumnAsync` is currently `static` with no access modifier — change it to `internal static` in Step 3 so the test can call it.

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test Zurari.Tests --filter "CreateColumnAsync_Headless_CompletesWithoutConsole"`
Expected: FAIL — either compile error (`CreateColumnAsync` inaccessible) or `System.IO.IOException: The handle is invalid` (Japanese: ハンドルが無効です) from `Console.WindowHeight` inside the loader task.

- [x] **Step 3: Add helpers and use them**

Add near other small static helpers in `Program`:

```csharp
// Console.WindowWidth/Height throw IOException when no console is attached (tests, redirected IO).
internal static int SafeWindowWidth()  { try { return Console.WindowWidth; }  catch { return 120; } }
internal static int SafeWindowHeight() { try { return Console.WindowHeight; } catch { return 30; } }
```

Replace `Console.WindowHeight` / `Console.WindowWidth` with the safe helpers at exactly these four places (leave all other uses alone — they are on real-console-only paths):

1. Program.cs:3641 (inside `CreateColumnAsync`'s loader): `int visibleHeight = Console.WindowHeight - 3;` → `int visibleHeight = SafeWindowHeight() - 3;`
2. Program.cs:3831 (`MoveUpAsync`): `int visibleHeight = Console.WindowHeight - 2;` → `int visibleHeight = SafeWindowHeight() - 2;`
3. Program.cs:3861 (`MoveDownAsync`): same replacement as above.
4. Program.cs:3987 (`UpdateHorizontalScroll`): `Console.WindowWidth / ColumnWidth` → `SafeWindowWidth() / ColumnWidth`

Also change the `CreateColumnAsync` declaration (3599) from `static Task<Column> CreateColumnAsync(...)` to `internal static Task<Column> CreateColumnAsync(...)`.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test Zurari.Tests --filter "CreateColumnAsync_Headless_CompletesWithoutConsole"`
Expected: PASS

- [x] **Step 5: Full verification and commit**

Run: `dotnet build && dotnet test`
Expected: build OK, all tests pass.

```bash
git add Program.cs Zurari.Tests/AsyncColumnLoadTests.cs
git commit -m "test(phase9): console-safe size helpers enable headless column-load tests"
```

---

### Task 2: Fix broken read cancellation — loader observes `column.ReadCts`

**Files:**
- Modify: `Program.cs` — `CreateColumnAsync` (3599-3775) and its 7 call sites (3515, 4223, 4460, 5399, 5500, 5538, plus the Task-1 test)
- Test: `Zurari.Tests/AsyncColumnLoadTests.cs`

- [x] **Step 1: Write the failing test**

Add to `AsyncColumnLoadTests`:

```csharp
[Fact]
public async Task CancelReadCts_StopsLoadBeforeCompletion()
{
    // 2000 files: loader needs several ms (enumerate+sort two phases); Cancel() below
    // runs within microseconds of CreateColumnAsync returning, so the loader cannot
    // plausibly have finished. If cancellation is wired correctly, Entries stays short.
    string dir = Path.Combine(Path.GetTempPath(), "zrr_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    for (int i = 0; i < 2000; i++)
        File.WriteAllText(Path.Combine(dir, $"f{i:D5}.txt"), "");
    try
    {
        Column col = await Program.CreateColumnAsync(dir);
        col.ReadCts!.Cancel();
        await col.LoadingTask!;          // must complete (cancelled), not hang or throw
        Assert.False(col.IsLoading);
        Assert.True(col.Entries.Count < 2000,
            $"expected cancelled read, but all {col.Entries.Count} entries were loaded");
    }
    finally { Directory.Delete(dir, true); }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test Zurari.Tests --filter "CancelReadCts_StopsLoadBeforeCompletion"`
Expected: FAIL on the `< 2000` assert — the loader observes the `ct` parameter (`CancellationToken.None`), so `ReadCts.Cancel()` does nothing and all 2000 entries load.

- [x] **Step 3: Implement — drop the `ct` parameter, observe `ReadCts.Token`**

In `CreateColumnAsync`:

1. Change signature: `internal static Task<Column> CreateColumnAsync(string path)` (remove `CancellationToken ct`).
2. After `column.ReadCts = new CancellationTokenSource();` (3634) add:
   ```csharp
   CancellationToken ct = column.ReadCts.Token;
   ```
   The whole loader body below already references `ct`, so no further body edits are needed **except**: change the two `Task.Run(..., ct)` invocations (outer at 3771, nested at 3753) to `Task.Run(...)` **without** the token argument. Reason: if the token were already cancelled, `Task.Run(…, token)` never runs the delegate, the `finally` never executes, and `IsLoading` would stay true forever. Cooperative `ct.ThrowIfCancellationRequested()` / `ct.IsCancellationRequested` checks inside the body are the cancellation mechanism.
3. Update all call sites — remove the second argument:
   - 3515 (`RefreshPaneNowAsync`): `CreateColumnAsync(current.Path)`
   - 4223 (rename handler): `CreateColumnAsync(col.Path)`
   - 4460 (`RefreshCurrentAsync`): `CreateColumnAsync(currentPath)`
   - 5399 (`RebuildRightSideAsync`): `CreateColumnAsync(nextPath)`
   - 5500, 5538 (`JumpToPathAsync`): `CreateColumnAsync(rootMarker)` / `CreateColumnAsync(accumulatedPath)`
   - Task-1 test if it still passes `CancellationToken.None`.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test Zurari.Tests --filter "CancelReadCts_StopsLoadBeforeCompletion"`
Expected: PASS

- [x] **Step 5: Full verification and commit**

Run: `dotnet build && dotnet test`

```bash
git add Program.cs Zurari.Tests/AsyncColumnLoadTests.cs
git commit -m "fix(phase9): directory-read cancellation was a no-op — loader now observes ReadCts"
```

---

### Task 3: Non-blocking `RebuildRightSideAsync` + idle-tick chain extension

Remove the `await LoadingTask` regression from `907b4b7`. The chain-building loop needs loaded entries to walk deeper (cursor-memory restore), so when it hits a still-loading column it stops; a new idle-tick function `ExtendRightSideIfReadyAsync` continues the chain after each load completes.

**Files:**
- Modify: `Program.cs` — `RebuildRightSideAsync` (5385-5409), main loop idle branch (2840-2874)
- Test: `Zurari.Tests/AsyncColumnLoadTests.cs`

- [x] **Step 1: Write the failing tests**

Add to `AsyncColumnLoadTests`:

```csharp
static async Task<Pane> MakePaneAtAsync(string rootDir)
{
    var pane = new Pane();
    Column col = await Program.CreateColumnAsync(rootDir);
    await col.LoadingTask!;
    pane.Columns.Add(col);
    return pane;
}

[Fact]
public async Task RebuildRightSide_DoesNotAwaitChildLoad()
{
    string root = MakeTempDir();
    string sub = Path.Combine(root, "sub");
    Directory.CreateDirectory(sub);
    for (int i = 0; i < 2000; i++)
        File.WriteAllText(Path.Combine(sub, $"f{i:D5}.txt"), "");
    try
    {
        Pane pane = await MakePaneAtAsync(root);
        pane.Columns[0].Selected = pane.Columns[0].Entries.IndexOf("sub/");
        Assert.True(pane.Columns[0].Selected >= 0);

        await Program.RebuildRightSideAsync(pane, 0);

        // Child column was added, and RebuildRightSideAsync returned while its
        // 2000-entry load is still in flight (old code awaited it → IsLoading false).
        Assert.Equal(2, pane.Columns.Count);
        Assert.True(pane.Columns[1].IsLoading, "RebuildRightSideAsync blocked on child LoadingTask");

        await pane.Columns[1].LoadingTask!;
        Assert.Equal(2000, pane.Columns[1].Entries.Count);
    }
    finally { Directory.Delete(root, true); }
}

[Fact]
public async Task ExtendRightSideIfReady_ContinuesChainAfterLoad()
{
    string root = MakeTempDir();
    string sub = Path.Combine(root, "sub");
    string grand = Path.Combine(sub, "grand");
    Directory.CreateDirectory(grand);
    File.WriteAllText(Path.Combine(grand, "leaf.txt"), "");
    try
    {
        Pane pane = await MakePaneAtAsync(root);
        pane.Columns[0].Selected = pane.Columns[0].Entries.IndexOf("sub/");
        await Program.RebuildRightSideAsync(pane, 0);
        await pane.Columns[1].LoadingTask!;
        pane.Columns[1].Selected = pane.Columns[1].Entries.IndexOf("grand/");

        await Program.ExtendRightSideIfReadyAsync(pane);

        Assert.Equal(3, pane.Columns.Count);
        Assert.EndsWith("grand", pane.Columns[2].Path);
    }
    finally { Directory.Delete(root, true); }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test Zurari.Tests --filter "RebuildRightSide_DoesNotAwaitChildLoad|ExtendRightSideIfReady_ContinuesChainAfterLoad"`
Expected: first FAILS on `IsLoading` assert (old code awaited the load); second fails to compile (`ExtendRightSideIfReadyAsync` doesn't exist). Comment the second test out to see the first fail, or accept the compile error as the failure signal for both.

- [x] **Step 3: Implement**

Replace `RebuildRightSideAsync` (5385-5409) with:

```csharp
internal static async Task RebuildRightSideAsync(Pane pane, int columnIndex)
{
    while (pane.Columns.Count > columnIndex + 1)
    {
        Column col = pane.Columns[pane.Columns.Count - 1];
        col.ReadCts?.Cancel();
        SaveCursorMemory(col);
        pane.Columns.RemoveAt(pane.Columns.Count - 1);
    }

    string? nextPath = GetSelectedDirectory(pane, columnIndex);

    while (nextPath != null)
    {
        Column next = await CreateColumnAsync(nextPath);
        pane.Columns.Add(next);
        // Entries aren't loaded yet, so the chain can't walk deeper here;
        // ExtendRightSideIfReadyAsync continues it on idle ticks once the load lands.
        if (next.IsLoading) break;
        nextPath = GetSelectedDirectory(pane, pane.Columns.Count - 1);
    }

    UpdateHorizontalScroll(pane);
}
```

(Only two changes vs current: `internal` modifier, and the removed `await next.LoadingTask` replaced by the `if (next.IsLoading) break;` after `Add`.)

Add below it:

```csharp
internal static async Task ExtendRightSideIfReadyAsync(Pane pane)
{
    if (pane.Columns.Count == 0) return;
    Column last = pane.Columns[pane.Columns.Count - 1];
    if (last.IsLoading) return;
    if (GetSelectedDirectory(pane, pane.Columns.Count - 1) == null) return;
    await RebuildRightSideAsync(pane, pane.Columns.Count - 1);
    _redrawGate.Request();
}
```

In the main loop idle branch, directly before `System.Threading.Thread.Sleep(50);` (2872), add:

```csharp
if (State.Dialog == null)
{
    foreach (Pane p in State.Panes)
        await ExtendRightSideIfReadyAsync(p);
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test Zurari.Tests --filter "RebuildRightSide_DoesNotAwaitChildLoad|ExtendRightSideIfReady_ContinuesChainAfterLoad"`
Expected: PASS (both)

- [x] **Step 5: Full verification and commit**

Run: `dotnet build && dotnet test`

```bash
git add Program.cs Zurari.Tests/AsyncColumnLoadTests.cs
git commit -m "fix(phase9): RebuildRightSideAsync no longer blocks on child directory load"
```

---

### Task 4: Cursor keys never touch the disk — defer rebuilds to the idle tick

MoveUp/MoveDown (and the shared leap-nav helper) currently decide per keystroke between "full rebuild" and "validate" — both of which start reads. New behavior: a cursor move only (a) cancels in-flight right-side reads, (b) synchronously prunes right columns that no longer match the selection, (c) flags the pane dirty. The idle branch performs the rebuild once the cursor has been stable for `NavigationDebounceMs` (300ms). During key repeat, `Console.KeyAvailable` is continuously true, so the idle branch never runs → zero reads during repeat. This is the behavior the user asked for.

**Files:**
- Modify: `Program.cs` — `Pane` class, `MoveUpAsync`/`MoveDownAsync` (3828-3886), `IsNavigationDebounced` (3888), `ValidateAndRebuildRightPaneIfNeeded` (3908), `RebuildRightSideOrValidateAsync` (4815), key dispatch call sites (3103, 3107), main loop idle branch
- Test: `Zurari.Tests/AsyncColumnLoadTests.cs`

- [x] **Step 1: Write the failing tests**

Add to `AsyncColumnLoadTests`:

```csharp
[Fact]
public async Task MoveDown_DuringRepeat_StartsNoReadAndFlagsPending()
{
    string root = MakeTempDir();
    Directory.CreateDirectory(Path.Combine(root, "d1"));
    Directory.CreateDirectory(Path.Combine(root, "d2"));
    try
    {
        Pane pane = await MakePaneAtAsync(root);
        pane.Columns[0].Selected = 0;                        // on "d1/"
        Program._lastNavigationTime = DateTime.UtcNow;       // simulate mid-repeat

        Program.MoveDown(pane);                              // now synchronous

        Assert.Equal(1, pane.Columns[0].Selected);           // cursor moved ("d2/")
        Assert.True(pane.NavRebuildPending);
        Assert.Single(pane.Columns);                         // no read started

        // Debounce not elapsed → idle processing does nothing
        await Program.ProcessPendingNavigationAsync(pane);
        Assert.True(pane.NavRebuildPending);
        Assert.Single(pane.Columns);

        // Cursor stable past debounce → idle processing rebuilds
        Program._lastNavigationTime = DateTime.UtcNow.AddMilliseconds(-NavigationTestHelpers.DebounceMs - 100);
        await Program.ProcessPendingNavigationAsync(pane);
        Assert.False(pane.NavRebuildPending);
        Assert.Equal(2, pane.Columns.Count);
        Assert.EndsWith("d2", pane.Columns[1].Path);
    }
    finally { Directory.Delete(root, true); }
}

[Fact]
public async Task MoveDown_OntoFile_PrunesStaleRightColumns()
{
    string root = MakeTempDir("zzz.txt");                    // sorts after directories
    Directory.CreateDirectory(Path.Combine(root, "d1"));
    try
    {
        Pane pane = await MakePaneAtAsync(root);
        pane.Columns[0].Selected = 0;                        // "d1/"
        await Program.RebuildRightSideAsync(pane, 0);
        await pane.Columns[1].LoadingTask!;
        Assert.Equal(2, pane.Columns.Count);

        Program.MoveDown(pane);                              // onto "zzz.txt"

        Assert.Single(pane.Columns);                         // stale right column pruned synchronously
    }
    finally { Directory.Delete(root, true); }
}
```

Add a tiny helper class in the same file (the debounce constant is private):

```csharp
static class NavigationTestHelpers
{
    public const int DebounceMs = 300; // mirrors Program.NavigationDebounceMs
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test Zurari.Tests --filter "MoveDown_DuringRepeat_StartsNoReadAndFlagsPending|MoveDown_OntoFile_PrunesStaleRightColumns"`
Expected: compile FAIL — `Program.MoveDown`, `Program.ProcessPendingNavigationAsync`, `Program._lastNavigationTime`, `pane.NavRebuildPending` don't exist yet.

- [x] **Step 3: Implement**

1. `Pane` class: add field `public bool NavRebuildPending;`

2. `_lastNavigationTime`: find its declaration (near `NavigationDebounceMs`, Program.cs:2753) and change accessibility to `internal static DateTime _lastNavigationTime;` (keep existing initializer if any).

3. Add these three functions (replacing `IsNavigationDebounced` at 3888-3896 and `ValidateAndRebuildRightPaneIfNeeded` at 3908-3941, both of which are **deleted**):

```csharp
// Called on every cursor move. Never touches the disk: cancels in-flight right-side
// reads, prunes right columns that no longer match the selection, and defers the
// rebuild to ProcessPendingNavigationAsync (idle tick, after the cursor is stable).
internal static void MarkNavDirty(Pane pane)
{
    CancelRightSideReads(pane, pane.ActiveColumn);
    PruneStaleRightColumns(pane, pane.ActiveColumn);
    pane.NavRebuildPending = true;
    _lastNavigationTime = DateTime.UtcNow;
}

static void PruneStaleRightColumns(Pane pane, int columnIndex)
{
    string? currentSelection = GetSelectedDirectory(pane, columnIndex);
    Column? right = pane.Columns.Count > columnIndex + 1 ? pane.Columns[columnIndex + 1] : null;
    // Keep a matching right column visible (avoids flicker, cf. commit f11252c);
    // prune when the cursor is on a file or the right column shows a different dir.
    if (right != null && currentSelection == right.Path) return;
    while (pane.Columns.Count > columnIndex + 1)
    {
        Column col = pane.Columns[pane.Columns.Count - 1];
        col.ReadCts?.Cancel();
        SaveCursorMemory(col);
        pane.Columns.RemoveAt(pane.Columns.Count - 1);
    }
}

internal static async Task ProcessPendingNavigationAsync(Pane pane)
{
    if (!pane.NavRebuildPending) return;
    if ((DateTime.UtcNow - _lastNavigationTime).TotalMilliseconds < NavigationDebounceMs) return;
    pane.NavRebuildPending = false;
    await RebuildRightSideAsync(pane, pane.ActiveColumn);
    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
    _redrawGate.Request();
}
```

4. Rewrite `MoveUpAsync` → synchronous `MoveUp` (and identically `MoveDownAsync` → `MoveDown`):

```csharp
internal static void MoveUp(Pane pane)
{
    Column c = pane.Columns[pane.ActiveColumn];
    int visibleHeight = SafeWindowHeight() - 2;

    if (c.Selected > 0)
    {
        c.Selected--;
        if (c.Selected < c.ScrollOffset)
            c.ScrollOffset = c.Selected;
        UpdateHorizontalScroll(pane);
        MarkNavDirty(pane);
        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
    }
}

internal static void MoveDown(Pane pane)
{
    Column c = pane.Columns[pane.ActiveColumn];
    int visibleHeight = SafeWindowHeight() - 2;

    if (c.Selected + 1 < c.Entries.Count)
    {
        c.Selected++;
        if (c.Selected >= c.ScrollOffset + visibleHeight)
            c.ScrollOffset = c.Selected - visibleHeight + 1;
        UpdateHorizontalScroll(pane);
        MarkNavDirty(pane);
        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
    }
}
```

(Note: `StartPreviewLoad` stays on the keystroke path — it is already fire-and-forget with cancellation, and per-keystroke preview was the pre-regression behavior. The extra call in `ProcessPendingNavigationAsync` refreshes it after the rebuild lands.)

5. Update the dispatch sites 3103/3107: `await MoveUpAsync(pane);` → `MoveUp(pane);` and `await MoveDownAsync(pane);` → `MoveDown(pane);`

6. Replace `RebuildRightSideOrValidateAsync` (4813-4826, delete it) — its one caller at 4810 (`await RebuildRightSideOrValidateAsync(pane);`) becomes `MarkNavDirty(pane);` (make the containing method non-async if nothing else awaits; if other awaits remain, leave it async).

7. Repeat-prone jump keys also switch to `MarkNavDirty`. In `HandleSpecialKeyAsync`, replace `await RebuildRightSideAsync(pane, pane.ActiveColumn);` with `MarkNavDirty(pane);` at exactly these sites (all follow a `NavigationHelper.*` cursor jump): 3220 (PageUp), 3227 (PageDown), 3234 (Home), 3241 (End), 3251 (g), 3259 (G), 3269 (Ctrl+B), 3287 (Ctrl+F). Leave every other `RebuildRightSideAsync` call site alone — Enter/Parent/search-jump/etc. are discrete actions that want the chain immediately, and they're non-blocking since Task 3.

8. Main loop idle branch: extend the Task-3 insertion to also process pending navigation. The block added before `Thread.Sleep(50)` becomes:

```csharp
if (State.Dialog == null)
{
    await ProcessPendingNavigationAsync(FocusedPane);
    foreach (Pane p in State.Panes)
        await ExtendRightSideIfReadyAsync(p);
}
```

(`ExtendRightSideIfReadyAsync` gains one guard as its first line: `if (pane.NavRebuildPending) return;` — don't extend a chain the debounced rebuild is about to replace.)

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test Zurari.Tests --filter "MoveDown_DuringRepeat_StartsNoReadAndFlagsPending|MoveDown_OntoFile_PrunesStaleRightColumns"`
Expected: PASS (both)

- [x] **Step 5: Full verification and commit**

Run: `dotnet build && dotnet test`
Expected: all green. If `NavigationTests.cs` referenced `MoveUpAsync`/`MoveDownAsync`, update those references to the sync names.

```bash
git add Program.cs Zurari.Tests/AsyncColumnLoadTests.cs Zurari.Tests/NavigationTests.cs
git commit -m "fix(phase9): cursor keys never start directory reads; rebuild deferred to idle tick"
```

---

### Task 5: Unblock the job-refresh and rename paths

`RefreshActiveJobColumnsAsync` is awaited every main-loop iteration (2835); while a copy/move job runs it can re-read visible directories every ~500ms — with `await LoadingTask` that stalls input on slow drives. The rename handler has the same blocking await.

**Files:**
- Modify: `Program.cs` — `RefreshPaneNowAsync` (3505-3527), rename handler (~4220-4230)
- Test: `Zurari.Tests/AsyncColumnLoadTests.cs`

- [x] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task RefreshPaneNow_DoesNotAwaitLoad_AndReconcilesMarksAfter()
{
    string root = MakeTempDir("a.txt", "b.txt");
    try
    {
        Pane pane = await MakePaneAtAsync(root);
        pane.Columns[0].Marked.Add("a.txt");
        pane.Columns[0].Marked.Add("ghost.txt"); // no longer on disk

        await Program.RefreshPaneNowAsync(pane);

        Column refreshed = pane.Columns[0];
        if (refreshed.LoadingTask != null) await refreshed.LoadingTask;
        // Mark reconciliation runs as a continuation of the load: poll briefly.
        for (int i = 0; i < 100 && refreshed.Marked.Contains("ghost.txt"); i++)
            await Task.Delay(10);
        Assert.Contains("a.txt", refreshed.Marked);
        Assert.DoesNotContain("ghost.txt", refreshed.Marked);
    }
    finally { Directory.Delete(root, true); }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test Zurari.Tests --filter "RefreshPaneNow_DoesNotAwaitLoad_AndReconcilesMarksAfter"`
Expected: compile FAIL (`RefreshPaneNowAsync` is private). The behavioral assertions pass on old code (it awaits, then reconciles) — the point of this test is to pin the marks contract so the non-blocking rewrite can't silently break it.

- [x] **Step 3: Implement**

In `RefreshPaneNowAsync` (make it `internal static`), replace the loop body block

```csharp
SaveCursorMemory(current);
Column refreshed = await CreateColumnAsync(current.Path);
if (refreshed.LoadingTask != null)
    await refreshed.LoadingTask;

refreshed.Marked = current.Marked;
MarkHelper.ReconcileAfterReload(refreshed);

pane.Columns[i] = refreshed;
deepestAffected = i;
```

with:

```csharp
SaveCursorMemory(current);
Column refreshed = await CreateColumnAsync(current.Path);
refreshed.Marked = current.Marked;
if (refreshed.LoadingTask != null)
{
    // Reconcile marks only once entries are known. Runs on a pool thread like the
    // loader itself; draw already tolerates concurrent Entries mutation, and Marked
    // reconciliation is the same class of benign race.
    _ = refreshed.LoadingTask.ContinueWith(_ =>
    {
        MarkHelper.ReconcileAfterReload(refreshed);
        _redrawGate.Request();
    }, TaskContinuationOptions.OnlyOnRanToCompletion);
}
else
{
    MarkHelper.ReconcileAfterReload(refreshed);
}
pane.Columns[i] = refreshed;
deepestAffected = i;
```

In the rename handler (~4223, `Column refreshed = await CreateColumnAsync(col.Path);` followed by an `await refreshed.LoadingTask`-style block): locate it via `git grep -n "LoadingTask" Program.cs`, and delete the `if (... LoadingTask != null) await ... LoadingTask;` lines there. The cursor restore to the renamed file happens via `SeekRestoreTo` in the loader's `finally`, so nothing else is needed. If the rename handler reads `refreshed.Entries` right after (e.g. to position the cursor), keep the await **only** in that case and note it in the plan checklist as a finding.

`RefreshCurrentAsync` (Ctrl+L, 4444-4471) is already fire-and-forget from the input loop's perspective (`_ = RefreshCurrentAsync(pane);`) — leave its internal await alone.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test Zurari.Tests --filter "RefreshPaneNow_DoesNotAwaitLoad_AndReconcilesMarksAfter"`
Expected: PASS

- [x] **Step 5: Full verification and commit**

Run: `dotnet build && dotnet test`

```bash
git add Program.cs Zurari.Tests/AsyncColumnLoadTests.cs
git commit -m "fix(phase9): job-refresh and rename no longer block the input loop on reloads"
```

---

### Task 6: Async split toggle, async startup, stat check off the caller thread

Three remaining synchronous holes, all painful on cloud drives:

**Files:**
- Modify: `Program.cs` — `ToggleSplitAsync` (5411-5452), startup (2809), `CreateColumnAsync` validity check (3625-3629)

- [x] **Step 1: `ToggleSplitAsync`** — replace `newPane.Columns.Add(CreateColumn(initialPath));` (5425) with:

```csharp
newPane.Columns.Add(await CreateColumnAsync(initialPath));
```

(The following `await RebuildRightSideAsync(newPane, 0);` is non-blocking since Task 3; with column 0 still loading it simply finds no selected directory and returns — `ExtendRightSideIfReadyAsync` builds the chain once the load lands.)

- [x] **Step 2: Startup** — at Program.cs:2809, replace the synchronous `CreateColumn(...)` call with `await CreateColumnAsync(...)` (Main is already async; the first frames will show `[loading N]` instead of a frozen blank screen on a slow start directory). Check the exact expression with `git grep -n "CreateColumn(" Program.cs` — only startup and `ToggleSplitAsync` use the sync variant; after this step `CreateColumn` (3542-3597) has no callers left. **Delete it.**

- [x] **Step 3: Move the stat check into the loader.** In `CreateColumnAsync`, delete the caller-thread block (3625-3629):

```csharp
if (!IsValidPath(path))
{
    column.IsLoading = false;
    return Task.FromResult(column);
}
```

and instead make it the first statement **inside** the loader's `try` (before the enumeration): 

```csharp
if (!IsValidPath(path)) return;
```

(`return` from the async lambda falls through to `finally`: `IsLoading` → false, `<No file>` placeholder added, redraw requested. Behavior change: an invalid/unreachable path now shows `<No file>` instead of a blank column — acceptable, arguably better. The reason for the move: `DirectoryInfo.Exists` on a OneDrive/Dropbox placeholder or dead UNC path can itself hang for seconds, and it currently runs on the input loop's thread.)

- [x] **Step 4: Full verification and commit**

Run: `dotnet build && dotnet test`
Expected: all green (Task 1/2/3 tests cover `CreateColumnAsync` semantics; the `<No file>` placeholder path is covered by the loader's existing `finally`).

```bash
git add Program.cs
git commit -m "fix(phase9): split toggle, startup, and path stat no longer block on slow drives"
```

---

### Task 7: Lower-pane preview fix (diagnosis-driven)

Known issue (deferred from Phase 7 testing, 2026-08-02): Shift+V preview renders only in the upper pane when split (Ctrl+W) is active. Static reading did **not** find the cause — the draw path (`DrawPaneToLines` → `DrawPreviewToLines`) is yOffset-parameterized and looks correct, and the Shift+V toggle targets the focused pane correctly. Do not guess: this task first makes the draw path testable, then uses a repro test to bisect draw-layer vs state-layer.

**Files:**
- Modify: `Program.cs` — `DrawPaneToLines` (5936-5987), possibly `DrawPreviewToLines` (6244-6320) or `StartPreviewLoad` (2983-3089)
- Test: `Zurari.Tests/PreviewSplitPaneTests.cs` (new file)

- [x] **Step 1: Make `DrawPaneToLines` console-independent.** At 5938, replace

```csharp
bool showPreview = pane.Preview.IsVisible && Console.WindowWidth >= ColumnWidth * 2;
```

with

```csharp
bool showPreview = pane.Preview.IsVisible && width >= ColumnWidth * 2;
```

(`width` is the function's own parameter; `BuildFrame` passes `width = Console.WindowWidth` for both panes, so runtime behavior is identical — this only removes the headless-test blocker.)

- [x] **Step 2: Add the repro test.** Create `Zurari.Tests/PreviewSplitPaneTests.cs`:

```csharp
using Xunit;
using Xunit.Abstractions;

public class PreviewSplitPaneTests
{
    readonly ITestOutputHelper _out;
    public PreviewSplitPaneTests(ITestOutputHelper output) { _out = output; }

    static Line[] NewLines(int n)
    {
        var lines = new Line[n];
        for (int i = 0; i < n; i++) lines[i] = new Line();
        return lines;
    }

    static Pane MakeFilePaneWithPreview()
    {
        var pane = new Pane();
        var col = new Column("C:\\test\\dir");
        col.Entries.Add("file.txt");
        col.Selected = 0;
        col.IsLoading = false;
        pane.Columns.Add(col);
        pane.Preview.IsVisible = true;
        pane.Preview.Content = new PreviewContent("Text", "42 B", "2026-01-01",
            new[] { "HELLO_PREVIEW_BODY" }, false);
        pane.Preview.IsLoading = false;
        return pane;
    }

    [Theory]
    [InlineData(0)]   // upper pane rect
    [InlineData(8)]   // lower pane rect (yOffset = topHeight + separator)
    public void Preview_RendersInsidePaneRect(int yOffset)
    {
        const int width = 80;
        const int height = 12;
        Line[] lines = NewLines(yOffset + height);
        Pane pane = MakeFilePaneWithPreview();

        Program.DrawPaneToLines(lines, pane, yOffset, height,
            focused: true, showOwnPathLine: true, width: width);

        for (int i = 0; i < lines.Length; i++)
            _out.WriteLine($"[{i:D2}] |{lines[i].Render(width)}|");

        bool found = false;
        for (int i = yOffset; i < lines.Length; i++)
            if (lines[i].Render(width).Contains("HELLO_PREVIEW_BODY")) found = true;
        Assert.True(found, $"preview body not rendered at yOffset={yOffset}");
    }
}
```

- [x] **Step 3: Run and branch on the outcome**

Run: `dotnet test Zurari.Tests --filter "Preview_RendersInsidePaneRect" --logger "console;verbosity=detailed"`

**Branch A — yOffset=8 FAILS (draw-layer bug):** the dumped frame localizes the broken row(s). Prime suspects, in order: (1) `DrawPreviewToLines` writing rows relative to the wrong base — every `lines[...]` index must be `yOffset + row`; (2) the `bodyFrameHeight = height + 1` interplay (5950) making `visibleHeight` overrun the pane rect so preview rows land on the path-line row and get overwritten; (3) an out-of-range guard silently skipping rows. Fix at the identified line, re-run until both theory cases pass, keep the test as the regression test.

**Branch B — both PASS (state-layer bug, draw exonerated):** the bug is in what state exists at draw time, not how it draws. Add a temporary debug dump to `BuildFrame`'s split branch (5797-5815) writing per-pane `Preview.IsVisible / IsLoading / Content?.TypeLabel / SelectedItemIsFileOrDrive(pane)` to a log file each frame; run the app, split (Ctrl+W), Tab to the lower pane, press Shift+V on a file, inspect the log. Prime suspects: `StartPreviewLoad`'s width/height math using full-screen `Console.WindowHeight` (3072), `UpdateHorizontalScroll`'s preview slot reservation, or the redraw arriving before `Content` is set with no follow-up redraw. Fix at the identified site, add a regression test capturing it, remove the debug dump.

**If the session cannot identify the root cause within either branch: STOP, record findings in "Unresolved issues" below, do not commit speculative fixes.**

- [x] **Step 4: Full verification and commit**

Run: `dotnet build && dotnet test`

```bash
git add Program.cs Zurari.Tests/PreviewSplitPaneTests.cs
git commit -m "fix(phase9): preview renders in the lower split pane"
```

---

### Task 8: Manual verification on a slow drive

Automated tests can't reproduce cloud-drive latency. Verify by hand before closing the phase:

- [ ] Build release: `dotnet build -c Release`
- [ ] Navigate to a genuinely slow directory — a OneDrive folder with online-only placeholders, a UNC/network share, or (fallback) a local directory with 100k+ files.
- [ ] Hold ↓ (key repeat) in a column of directories: the cursor must move at full repeat speed with no stutter; **no** `[loading N]` counters should churn during the repeat (reads are paused).
- [ ] Release the key: after ~300ms the right column appears with a live `[loading N]` counter that visibly counts up.
- [ ] Press ↓ again mid-load: the load is abandoned instantly (counter column disappears), cursor moves immediately.
- [ ] Ctrl+W split while the active column is on the slow drive: UI stays responsive; the new pane's columns fill in as loads land.
- [ ] Start a copy job onto the slow drive, then hold ↓ during the job: cursor speed unaffected (Task 5).
- [ ] Shift+V in the lower split pane shows the preview (Task 7).
- [ ] Record results here (pass/fail per bullet + drive type used).

---

## Execution log (2026-08-05, subagent-driven, branch worktree-phase9-slow-drive)

| Task | Commit(s) | Evidence |
|---|---|---|
| 1 | `987989d` | red: CS0117 inaccessible; green: 163/163 |
| 2 | `e26d15b` | red: "all 2000 entries were loaded" behavioral proof; green: 164/164 |
| 3 | `b1fa62d` | red: "RebuildRightSideAsync blocked on child LoadingTask"; green: 166/166 |
| 4 | `592242e`, `cb41102` | red: 9 missing-symbol compile errors; green: 168/168, 0 warnings after CS1998 cleanup |
| 5 | `cd529ae` | red: CS0117; green: 169/169. Rename handler kept its await — `SeekRestoreTo` needs loaded entries to seek to the new name (documented in-plan conditional) |
| 6 | `eb3c6b5` | red: LoadingTask null on invalid path; green: 170/170. Sync `CreateColumn` deleted (0 callers) |
| 7 | `69afc1d` | Branch B: both repro tests PASS — draw layer and BuildFrame exonerated with regression tests; green: 173/173 |

Every task: spec-compliance review + quality review passed; `dotnet build` 0 warnings / 0 errors at each commit.

## Unresolved issues

(Execution sessions: record anything the plan didn't anticipate here and stop rather than improvise, per the project's execution regime.)

- **Task 7 / lower-pane preview: root cause NOT yet found.** Headless evidence (PreviewSplitPaneTests) proves `DrawPaneToLines` renders preview correctly at nonzero yOffset AND `BuildFrame` with `SplitActive=true, FocusedPane=1` includes the lower pane's preview in the frame. Remaining suspects are runtime-only and need a live terminal repro: (1) `StartPreviewLoad` width/height captured from real console at keypress time; (2) live focus/key routing to `State.Panes[1]` on Shift+V; (3) redraw coalescing/PrevFrame diff timing vs the background preview load. During manual verification (Task 8): note whether even the "Loading…" placeholder appears in the lower pane — if not, the toggle/draw state is wrong (suspect 2); if Loading appears but content never does, it's load/redraw timing (suspect 3).
- `SeekRestoreTo` contains a comment "retry once entries arrive" but no retry mechanism is wired; the rename handler compensates by awaiting the load (only remaining intentional await on a user-action path besides `JumpToPathAsync`). Candidate Phase 10 cleanup.

## Completion checklist

- [ ] All 8 tasks committed, `dotnet build` and `dotnet test` green at each commit
- [ ] Manual slow-drive checklist (Task 8) recorded with evidence
- [ ] Known-issue memory for lower-pane preview updated/closed
