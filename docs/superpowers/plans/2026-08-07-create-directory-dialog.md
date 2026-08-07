# Create Directory Dialog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `Shift+K` "New Folder" dialog that creates a subdirectory in the active pane's current directory, validated against Windows filename rules, with a cursor-safe scrolling text field, correct centering in split mode, and cross-pane refresh — while fixing the same cursor-scrolling and cross-pane-refresh gaps in the existing Rename dialog.

**Architecture:** Everything lives in the single `Program.cs` file, following this codebase's established convention (see `CLAUDE.md`). New pure/testable logic (scroll-window math, pane Y-range math, filename validation) is added as small `internal static` functions so it can be unit-tested directly from `Zurari.Tests` via `InternalsVisibleTo`, matching the existing pattern used by `TextInputBuffer`, `DialogLayout`, and `GetOperationTargets`. UI wiring (key-dispatch switches, dialog callbacks) is not unit-tested, matching this codebase's existing convention — none of the current dialog key handlers (`HandleConfirmDialogKey`, `HandleTextInputDialogKey`, etc.) have direct tests either; those are covered by the manual verification pass (Task 10).

**Tech Stack:** C#/.NET 8, xUnit (`Zurari.Tests/`).

**Reference spec:** `docs/superpowers/specs/2026-08-07-create-directory-dialog-design.md` — read this first if anything below is ambiguous; it explains *why* each piece is designed this way, including the two pre-existing gaps (cursor-truncation bug, missing cross-pane refresh) this plan also fixes.

---

## Task 1: `TextInputBuffer.ComputeScrollWindow` — cursor-safe horizontal scroll math

**Files:**
- Modify: `Program.cs` (inside `static class TextInputBuffer`, after `DisplayColumnAtCursor`, currently ending around line 1513)
- Test: `Zurari.Tests/TextInputBufferTests.cs`

This is the pure function that decides which slice of a long buffer is visible in a width-capped dialog field, keeping the cursor always inside the visible slice. It measures using `CharacterWidth.GetStringWidth` (already used throughout the codebase for CJK-aware width), not raw character count.

- [ ] **Step 1: Write the failing tests**

Append to `Zurari.Tests/TextInputBufferTests.cs` (inside the existing `public class TextInputBufferTests { ... }`, before its closing brace):

```csharp
    [Fact]
    public void ComputeScrollWindow_ShortBuffer_ShowsWholeBufferNoScroll()
    {
        var (start, end) = TextInputBuffer.ComputeScrollWindow("short", 3, 50);
        Assert.Equal(0, start);
        Assert.Equal(5, end);
    }

    [Fact]
    public void ComputeScrollWindow_LongBuffer_CursorAtEnd_RightAlignsWindow()
    {
        string buffer = new string('a', 60);
        var (start, end) = TextInputBuffer.ComputeScrollWindow(buffer, 60, 50);
        Assert.Equal(10, start);
        Assert.Equal(60, end);
    }

    [Fact]
    public void ComputeScrollWindow_LongBuffer_CursorAtStart_ShowsFromBeginning()
    {
        string buffer = new string('a', 60);
        var (start, end) = TextInputBuffer.ComputeScrollWindow(buffer, 0, 50);
        Assert.Equal(0, start);
        Assert.Equal(50, end);
    }

    [Fact]
    public void ComputeScrollWindow_LongBuffer_CursorInMiddle_WithinReachOfStart_ShowsFromBeginning()
    {
        string buffer = new string('a', 60);
        var (start, end) = TextInputBuffer.ComputeScrollWindow(buffer, 30, 50);
        Assert.Equal(0, start);
        Assert.Equal(50, end);
    }

    [Fact]
    public void ComputeScrollWindow_LongBuffer_CursorPastReach_ScrollsRight()
    {
        string buffer = new string('a', 60);
        var (start, end) = TextInputBuffer.ComputeScrollWindow(buffer, 55, 50);
        Assert.Equal(5, start);
        Assert.Equal(55, end);
    }

    [Fact]
    public void ComputeScrollWindow_CjkDoubleWidthChars_ScrollsByDisplayColumnNotCharIndex()
    {
        string buffer = new string('漢', 30); // 30 chars, display width 60 (each char is width 2)
        var (start, end) = TextInputBuffer.ComputeScrollWindow(buffer, 30, 50);
        Assert.Equal(5, start);
        Assert.Equal(30, end);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ComputeScrollWindow`
Expected: build error — `ComputeScrollWindow` does not exist on `TextInputBuffer` yet.

- [ ] **Step 3: Implement `ComputeScrollWindow`**

In `Program.cs`, inside `static class TextInputBuffer`, add this method after `DisplayColumnAtCursor` (before the class's closing brace, currently line 1514):

```csharp
    // Returns the [Start, End) character-index range of `buffer` that fits within `maxWidth`
    // display columns while always including `cursorPos`. Used to keep the cursor visible in a
    // width-capped dialog field instead of letting it scroll off-screen (see spec: "Cursor
    // visibility & horizontal scrolling").
    public static (int Start, int End) ComputeScrollWindow(string buffer, int cursorPos, int maxWidth)
    {
        int totalWidth = CharacterWidth.GetStringWidth(buffer);
        if (totalWidth <= maxWidth)
            return (0, buffer.Length);

        int cursorCol = CharacterWidth.GetStringWidth(buffer.Substring(0, cursorPos));

        int start;
        if (cursorCol <= maxWidth)
        {
            start = 0;
        }
        else
        {
            // Right-align: walk backward from the cursor until one more character would
            // overflow maxWidth.
            start = cursorPos;
            int width = 0;
            while (start > 0)
            {
                int w = CharacterWidth.GetStringWidth(buffer[start - 1].ToString());
                if (width + w > maxWidth) break;
                width += w;
                start--;
            }
        }

        int startCol = CharacterWidth.GetStringWidth(buffer.Substring(0, start));
        int end = start;
        int endCol = startCol;
        while (end < buffer.Length)
        {
            int w = CharacterWidth.GetStringWidth(buffer[end].ToString());
            if (endCol - startCol + w > maxWidth) break;
            endCol += w;
            end++;
        }

        return (start, end);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ComputeScrollWindow`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add Program.cs Zurari.Tests/TextInputBufferTests.cs
git commit -m "feat(dialog): add TextInputBuffer.ComputeScrollWindow for cursor-safe field scrolling"
```

---

## Task 2: `Program.ComputeFocusedPaneYRange` — pane sub-rectangle math for dialog centering

**Files:**
- Modify: `Program.cs` (add near `ComputeSplitPaneHeights`, currently lines 2973-2979)
- Test: `Zurari.Tests/DualPaneTests.cs`

Pure function computing the Y-range of the focused pane's sub-rectangle, so a dialog can be centered within it instead of the whole screen.

- [ ] **Step 1: Write the failing tests**

Append to `Zurari.Tests/DualPaneTests.cs`, inside `public class DualPaneTests { ... }`, before its closing brace:

```csharp
    [Fact]
    public void ComputeFocusedPaneYRange_NotSplit_ReturnsFullScreen()
    {
        var (y, height) = Program.ComputeFocusedPaneYRange(splitActive: false, focusedPane: 0, screenHeight: 40);
        Assert.Equal(0, y);
        Assert.Equal(40, height);
    }

    [Fact]
    public void ComputeFocusedPaneYRange_Split_FocusedUpper_ReturnsTopHalf()
    {
        (int topHeight, _) = Program.ComputeSplitPaneHeights(40);
        var (y, height) = Program.ComputeFocusedPaneYRange(splitActive: true, focusedPane: 0, screenHeight: 40);
        Assert.Equal(0, y);
        Assert.Equal(topHeight, height);
    }

    [Fact]
    public void ComputeFocusedPaneYRange_Split_FocusedLower_ReturnsBottomHalfWithSeparatorOffset()
    {
        (int topHeight, int bottomHeight) = Program.ComputeSplitPaneHeights(40);
        var (y, height) = Program.ComputeFocusedPaneYRange(splitActive: true, focusedPane: 1, screenHeight: 40);
        Assert.Equal(topHeight + 1, y);
        Assert.Equal(bottomHeight, height);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ComputeFocusedPaneYRange`
Expected: build error — `ComputeFocusedPaneYRange` does not exist yet.

- [ ] **Step 3: Implement `ComputeFocusedPaneYRange`**

In `Program.cs`, add this method immediately after `ComputeSplitPaneHeights` (currently ending at line 2979):

```csharp
    // Y-range of the focused pane's own sub-rectangle within the full screen, so overlays
    // (currently: the dialog box) can center within the pane the user is looking at instead of
    // the whole screen when split mode is active. See spec: "Centering within the active pane".
    internal static (int Y, int Height) ComputeFocusedPaneYRange(bool splitActive, int focusedPane, int screenHeight)
    {
        if (!splitActive)
            return (0, screenHeight);

        (int topHeight, int bottomHeight) = ComputeSplitPaneHeights(screenHeight);
        if (focusedPane == 0)
            return (0, topHeight);
        return (topHeight + 1, bottomHeight);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ComputeFocusedPaneYRange`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Program.cs Zurari.Tests/DualPaneTests.cs
git commit -m "feat(dialog): add ComputeFocusedPaneYRange for per-pane dialog centering"
```

---

## Task 3: Directory-name validation — pure functions

**Files:**
- Modify: `Program.cs` (add near `BuildFullPath`/`SelectedIsDeletable`, currently lines 3997-4015)
- Test: `Zurari.Tests/CreateDirectoryValidationTests.cs` (new file)

Three independent pure functions: character-filter predicate (used as-you-type), reserved-name/trailing-dot-space check (used on Enter), and collision check against already-loaded entries.

- [ ] **Step 1: Write the failing tests**

Create `Zurari.Tests/CreateDirectoryValidationTests.cs`:

```csharp
using Xunit;

public class CreateDirectoryValidationTests
{
    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void IsValidDirNameChar_RejectsWindowsInvalidChars_OnWindows(char c)
    {
        if (!Program.IsWindows()) return; // this rule is Windows-specific; nothing to assert elsewhere
        Assert.False(Program.IsValidDirNameChar(c));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('Z')]
    [InlineData('0')]
    [InlineData(' ')]
    [InlineData('-')]
    [InlineData('_')]
    [InlineData('漢')]
    public void IsValidDirNameChar_AcceptsOrdinaryAndCjkChars(char c)
    {
        Assert.True(Program.IsValidDirNameChar(c));
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsReservedDeviceName()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("CON", out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsReservedDeviceNameCaseInsensitive()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("com1", out _));
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsReservedNameWithExtension()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("NUL.txt", out _));
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsTrailingDot()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("folder.", out _));
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsTrailingSpace()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("folder ", out _));
    }

    [Fact]
    public void IsInvalidDirectoryName_AcceptsOrdinaryName()
    {
        Assert.False(Program.IsInvalidDirectoryName("New Folder", out _));
    }

    [Fact]
    public void DirectoryNameCollides_MatchesExistingDirEntryCaseInsensitive()
    {
        var entries = new List<string> { "a.txt", "Sub/", "b.txt" };
        Assert.True(Program.DirectoryNameCollides(entries, "sub"));
    }

    [Fact]
    public void DirectoryNameCollides_MatchesExistingFileEntry()
    {
        var entries = new List<string> { "a.txt", "Sub/" };
        Assert.True(Program.DirectoryNameCollides(entries, "A.TXT"));
    }

    [Fact]
    public void DirectoryNameCollides_NoMatch_ReturnsFalse()
    {
        var entries = new List<string> { "a.txt", "Sub/" };
        Assert.False(Program.DirectoryNameCollides(entries, "NewFolder"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CreateDirectoryValidationTests`
Expected: build error — `IsValidDirNameChar`, `IsInvalidDirectoryName`, `DirectoryNameCollides` don't exist yet.

- [ ] **Step 3: Implement the validation functions**

In `Program.cs`, add these immediately after `BuildFullPath` (currently ending at line 4003, before `SelectedIsDeletable`):

```csharp
    static readonly char[] WindowsInvalidDirNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    // Control characters are already rejected by the caller (HandleTextInputDialogKey's
    // existing `!char.IsControl(key.KeyChar)` guard) before this runs, so this only needs to
    // cover each platform's printable-character restrictions.
    internal static bool IsValidDirNameChar(char c)
    {
        if (IsWindows())
            return Array.IndexOf(WindowsInvalidDirNameChars, c) < 0;
        return c != '/';
    }

    static readonly HashSet<string> WindowsReservedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Checks the Windows-specific rules that can't be caught per-keystroke because they depend
    // on the whole name: reserved device names (with or without an extension) and a trailing
    // dot/space (Windows silently strips/rejects these).
    internal static bool IsInvalidDirectoryName(string name, out string reason)
    {
        if (IsWindows())
        {
            if (name.EndsWith('.') || name.EndsWith(' '))
            {
                reason = "cannot end with a dot or space";
                return true;
            }
            int dotIndex = name.IndexOf('.');
            string baseName = dotIndex >= 0 ? name.Substring(0, dotIndex) : name;
            if (WindowsReservedDirNames.Contains(baseName))
            {
                reason = "is a reserved name";
                return true;
            }
        }
        reason = "";
        return false;
    }

    internal static bool DirectoryNameCollides(List<string> entries, string name)
    {
        foreach (string entry in entries)
        {
            string entryName = entry.EndsWith("/") ? entry.Substring(0, entry.Length - 1) : entry;
            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter CreateDirectoryValidationTests`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add Program.cs Zurari.Tests/CreateDirectoryValidationTests.cs
git commit -m "feat(dialog): add directory-name validation functions"
```

---

## Task 4: `TextInputDialogState` — `CharFilter` and `HintText`, wired into the key handler

**Files:**
- Modify: `Program.cs`:
  - `TextInputDialogState` class (lines 1325-1330)
  - `ShowTextInput` (lines 4897-4906)
  - `HandleTextInputDialogKey` (lines 5101-5161)

No new unit tests in this task — `HandleTextInputDialogKey` is UI-dispatch code that follows the same untested convention as the other dialog key handlers in this file (`HandleConfirmDialogKey`, `HandleListSelectDialogKey`, none of which have direct tests either). It's covered by the manual verification pass (Task 10). This task is verified by a successful build.

- [ ] **Step 1: Add the two new fields to `TextInputDialogState`**

Replace:

```csharp
sealed class TextInputDialogState : DialogState
{
    public string Buffer = "";
    public int CursorPos;
    public Action<string?> OnResult = _ => { }; // null=キャンセル
}
```

With:

```csharp
sealed class TextInputDialogState : DialogState
{
    public string Buffer = "";
    public int CursorPos;
    public Action<string?> OnResult = _ => { }; // null=キャンセル
    public Func<char, bool>? CharFilter;         // null = accept all (Rename's existing behavior)
    public string HintText = "[Enter]Confirm  [Esc]Cancel";
}
```

- [ ] **Step 2: Extend `ShowTextInput` with optional parameters**

Replace:

```csharp
    static void ShowTextInput(string title, string initialValue, Action<string?> onResult)
    {
        State.Dialog = new TextInputDialogState
        {
            Title = title,
            Buffer = initialValue,
            CursorPos = initialValue.Length,
            OnResult = onResult,
        };
    }
```

With:

```csharp
    static void ShowTextInput(string title, string initialValue, Action<string?> onResult,
        Func<char, bool>? charFilter = null, string hintText = "[Enter]Confirm  [Esc]Cancel")
    {
        State.Dialog = new TextInputDialogState
        {
            Title = title,
            Buffer = initialValue,
            CursorPos = initialValue.Length,
            OnResult = onResult,
            CharFilter = charFilter,
            HintText = hintText,
        };
    }
```

Existing call sites (Rename, the conflict-resolution rename flow) don't pass the new arguments, so they keep their current behavior unchanged.

- [ ] **Step 3: Add Ctrl+U (clear) and the `CharFilter` check to `HandleTextInputDialogKey`**

Replace:

```csharp
        if (key.Key == ConsoleKey.End)
        {
            dialog.CursorPos = TextInputBuffer.End(dialog.Buffer, dialog.CursorPos);
            return;
        }

        // 文字受け取り: HandleSearchKeyAsync と同一の判定(制御文字を除外)。
        // IME確定文字・サロゲートペアもここを通り、TextInputBuffer.Insert が
        // string.Insert で正しく結合する(Program.cs の HandleSearchKeyAsync 実装を参照)。
        if (!char.IsControl(key.KeyChar))
        {
            (dialog.Buffer, dialog.CursorPos) = TextInputBuffer.Insert(dialog.Buffer, dialog.CursorPos, key.KeyChar);
            return;
        }
```

With:

```csharp
        if (key.Key == ConsoleKey.End)
        {
            dialog.CursorPos = TextInputBuffer.End(dialog.Buffer, dialog.CursorPos);
            return;
        }

        if (key.Key == ConsoleKey.U && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            dialog.Buffer = "";
            dialog.CursorPos = 0;
            return;
        }

        // 文字受け取り: HandleSearchKeyAsync と同一の判定(制御文字を除外)。
        // IME確定文字・サロゲートペアもここを通り、TextInputBuffer.Insert が
        // string.Insert で正しく結合する(Program.cs の HandleSearchKeyAsync 実装を参照)。
        if (!char.IsControl(key.KeyChar) && (dialog.CharFilter == null || dialog.CharFilter(key.KeyChar)))
        {
            (dialog.Buffer, dialog.CursorPos) = TextInputBuffer.Insert(dialog.Buffer, dialog.CursorPos, key.KeyChar);
            return;
        }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Run the full test suite to verify no regressions**

Run: `dotnet test`
Expected: all tests still PASS (same count as before this task, plus Tasks 1-3's new tests).

- [ ] **Step 6: Commit**

```bash
git add Program.cs
git commit -m "feat(dialog): add CharFilter and HintText to TextInputDialogState, wire Ctrl+U clear"
```

---

## Task 5: Cursor-safe scrolling rendering for `TextInputDialogState`

**Files:**
- Modify: `Program.cs`, `BuildDialogContentLines`'s `TextInputDialogState` case (lines 6321-6334)

Depends on Task 1 (`ComputeScrollWindow`) and Task 4 (`HintText`). Fixes the cursor-truncation bug described in the spec: the dialog no longer grows unbounded and silently drops the cursor marker on long input.

- [ ] **Step 1: Replace the `TextInputDialogState` case**

Replace:

```csharp
            case TextInputDialogState textInput:
            {
                string beforeCursor = textInput.Buffer.Substring(0, textInput.CursorPos);
                string atAndAfterCursor = textInput.Buffer.Substring(textInput.CursorPos);
                // 反転スペースだけだと配色によっては見えにくいため、実体のある全角ブロックを
                // 反転表示することで、入力欄であることとカーソル位置の両方を分かりやすくする。
                string displayLine = beforeCursor + "\x1b[7m█\x1b[0m" + atAndAfterCursor;
                return new List<string>
                {
                    displayLine,
                    "",
                    "[Enter]Confirm  [Esc]Cancel",
                };
            }
```

With:

```csharp
            case TextInputDialogState textInput:
            {
                // Cap the field at a fixed width and scroll so the cursor is always visible,
                // instead of letting the dialog grow to the buffer's full width and truncating
                // (which used to silently drop the cursor marker on long input — see spec:
                // "Cursor visibility & horizontal scrolling"). -1 reserves a column for the
                // cursor marker itself, which is inserted after slicing.
                const int MaxFieldWidth = 50;
                (int start, int end) = TextInputBuffer.ComputeScrollWindow(textInput.Buffer, textInput.CursorPos, MaxFieldWidth - 1);
                string visible = textInput.Buffer.Substring(start, end - start);
                int cursorInVisible = textInput.CursorPos - start;
                string beforeCursor = visible.Substring(0, cursorInVisible);
                string atAndAfterCursor = visible.Substring(cursorInVisible);
                // 反転スペースだけだと配色によっては見えにくいため、実体のある全角ブロックを
                // 反転表示することで、入力欄であることとカーソル位置の両方を分かりやすくする。
                string displayLine = beforeCursor + "\x1b[7m█\x1b[0m" + atAndAfterCursor;
                return new List<string>
                {
                    displayLine,
                    "",
                    textInput.HintText,
                };
            }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Run the full test suite to verify no regressions**

Run: `dotnet test`
Expected: all tests PASS, same count as after Task 4.

- [ ] **Step 4: Commit**

```bash
git add Program.cs
git commit -m "fix(dialog): cap TextInputDialogState field width and scroll to keep cursor visible"
```

---

## Task 6: Per-pane dialog centering

**Files:**
- Modify: `Program.cs`, `ComposeDialogIntoFrame` (lines 6417-6445) and `DrawDialogOverlay` (lines 5901-5905)

Depends on Task 2 (`ComputeFocusedPaneYRange`).

- [ ] **Step 1: Add a `yOffset` parameter to `ComposeDialogIntoFrame` and apply it to every row write**

Replace:

```csharp
    static void ComposeDialogIntoFrame(string[] frame, DialogState dialog, int width, int height)
    {
        List<string> contentLines = BuildDialogContentLines(dialog);
        int contentWidth = 0;
        foreach (string l in contentLines)
            contentWidth = Math.Max(contentWidth, CharacterWidth.GetStringWidth(StripAnsiForWidth(l)));

        // タイトルもコンテンツ幅の下限に含める("┤ タイトル ├" 分の余白を確保)
        int titleWidth = CharacterWidth.GetStringWidth(dialog.Title) + 4;
        contentWidth = Math.Max(contentWidth, titleWidth);

        DialogRect rect = DialogLayout.CalcDialogRect(contentWidth, contentLines.Count, width, height);
        int innerWidth = rect.Width - 2;
        int innerHeight = rect.Height - 2;

        string topBorder = BuildTitledTopBorder(dialog.Title, innerWidth);
        OverwriteFrameRow(frame, rect.Y, rect.X, topBorder, width, height);

        for (int i = 0; i < innerHeight; i++)
        {
            string rowText = i < contentLines.Count ? contentLines[i] : "";
            string rowText2 = FitRowToWidth(rowText, innerWidth);
            string row = "│" + rowText2 + "│";
            OverwriteFrameRow(frame, rect.Y + 1 + i, rect.X, row, width, height);
        }

        string bottomBorder = "└" + new string('─', innerWidth) + "┘";
        OverwriteFrameRow(frame, rect.Y + rect.Height - 1, rect.X, bottomBorder, width, height);
    }
```

With:

```csharp
    // `paneHeight` is the sub-rectangle height to center within (the focused pane's height in
    // split mode, or the full screen otherwise — see ComputeFocusedPaneYRange). `yOffset` is
    // added to every absolute row this writes, so the centered box lands inside that
    // sub-rectangle instead of always starting at the top of the screen. Row-bounds checking
    // still uses `frame.Length` (the real total frame height), not `paneHeight`.
    static void ComposeDialogIntoFrame(string[] frame, DialogState dialog, int width, int paneHeight, int yOffset)
    {
        List<string> contentLines = BuildDialogContentLines(dialog);
        int contentWidth = 0;
        foreach (string l in contentLines)
            contentWidth = Math.Max(contentWidth, CharacterWidth.GetStringWidth(StripAnsiForWidth(l)));

        // タイトルもコンテンツ幅の下限に含める("┤ タイトル ├" 分の余白を確保)
        int titleWidth = CharacterWidth.GetStringWidth(dialog.Title) + 4;
        contentWidth = Math.Max(contentWidth, titleWidth);

        DialogRect rect = DialogLayout.CalcDialogRect(contentWidth, contentLines.Count, width, paneHeight);
        int innerWidth = rect.Width - 2;
        int innerHeight = rect.Height - 2;

        string topBorder = BuildTitledTopBorder(dialog.Title, innerWidth);
        OverwriteFrameRow(frame, yOffset + rect.Y, rect.X, topBorder, width, frame.Length);

        for (int i = 0; i < innerHeight; i++)
        {
            string rowText = i < contentLines.Count ? contentLines[i] : "";
            string rowText2 = FitRowToWidth(rowText, innerWidth);
            string row = "│" + rowText2 + "│";
            OverwriteFrameRow(frame, yOffset + rect.Y + 1 + i, rect.X, row, width, frame.Length);
        }

        string bottomBorder = "└" + new string('─', innerWidth) + "┘";
        OverwriteFrameRow(frame, yOffset + rect.Y + rect.Height - 1, rect.X, bottomBorder, width, frame.Length);
    }
```

- [ ] **Step 2: Update `DrawDialogOverlay` to compute and pass the pane's Y-range**

Replace:

```csharp
    static void DrawDialogOverlay(string[] frame, int width, int height)
    {
        if (State.Dialog == null) return;
        ComposeDialogIntoFrame(frame, State.Dialog, width, height);
    }
```

With:

```csharp
    static void DrawDialogOverlay(string[] frame, int width, int height)
    {
        if (State.Dialog == null) return;

        (int dialogY, int dialogHeight) = ComputeFocusedPaneYRange(State.SplitActive, State.FocusedPane, height);
        ComposeDialogIntoFrame(frame, State.Dialog, width, dialogHeight, dialogY);
    }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Run the full test suite to verify no regressions**

Run: `dotnet test`
Expected: all tests PASS, same count as after Task 5.

- [ ] **Step 5: Commit**

```bash
git add Program.cs
git commit -m "fix(dialog): center dialog within the focused pane instead of the whole screen"
```

---

## Task 7: `RefreshOtherPanesAsync` — cross-pane refresh (used by both Rename and Create Directory)

**Files:**
- Modify: `Program.cs` (add near `RefreshAffectedColumnsAsync`/`RefreshPaneNowAsync`, currently lines 3517-3577); modify `ExecuteRenameAsync` (lines 4156-4199)
- Test: `Zurari.Tests/CrossPaneRefreshTests.cs` (new file)

Fixes the gap described in the spec: today, only background-job completion refreshes a second pane showing the same directory. Rename (and Create Directory, wired in Task 9) are synchronous and never did this, so a second pane goes stale. This task adds the shared helper and wires it into Rename; Task 9 wires it into Create Directory.

- [ ] **Step 1: Write the failing tests**

Create `Zurari.Tests/CrossPaneRefreshTests.cs`:

```csharp
using Xunit;

public class CrossPaneRefreshTests
{
    [Fact]
    public async Task RefreshOtherPanesAsync_OtherPaneShowsSameDir_RefreshesIt()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        var savedPanes = Program.State.Panes;
        try
        {
            Column colA = await Program.CreateColumnAsync(dir);
            if (colA.LoadingTask != null) await colA.LoadingTask;
            var paneA = new Pane();
            paneA.Columns.Add(colA);

            Column colB = await Program.CreateColumnAsync(dir);
            if (colB.LoadingTask != null) await colB.LoadingTask;
            var paneB = new Pane();
            paneB.Columns.Add(colB);

            Program.State.Panes = new List<Pane> { paneA, paneB };

            // Simulate a directory appearing on disk after both panes already loaded their listing.
            Directory.CreateDirectory(Path.Combine(dir, "NewFolder"));

            await Program.RefreshOtherPanesAsync(paneA, dir);

            Assert.Contains("NewFolder/", paneB.Columns[0].Entries);
        }
        finally
        {
            Program.State.Panes = savedPanes;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RefreshOtherPanesAsync_SkipsActingPane()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        var savedPanes = Program.State.Panes;
        try
        {
            Column colA = await Program.CreateColumnAsync(dir);
            if (colA.LoadingTask != null) await colA.LoadingTask;
            var paneA = new Pane();
            paneA.Columns.Add(colA);
            Program.State.Panes = new List<Pane> { paneA };

            Column originalColumnInstance = paneA.Columns[0];
            await Program.RefreshOtherPanesAsync(paneA, dir);

            // actingPane is skipped entirely, so its Column instance must be untouched.
            Assert.Same(originalColumnInstance, paneA.Columns[0]);
        }
        finally
        {
            Program.State.Panes = savedPanes;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RefreshOtherPanesAsync_OtherPaneShowsUnrelatedDir_NotRefreshed()
    {
        string dirA = AsyncColumnLoadTests.MakeTempDir("a.txt");
        string dirB = AsyncColumnLoadTests.MakeTempDir("b.txt");
        var savedPanes = Program.State.Panes;
        try
        {
            Column colA = await Program.CreateColumnAsync(dirA);
            if (colA.LoadingTask != null) await colA.LoadingTask;
            var paneA = new Pane();
            paneA.Columns.Add(colA);

            Column colB = await Program.CreateColumnAsync(dirB);
            if (colB.LoadingTask != null) await colB.LoadingTask;
            var paneB = new Pane();
            paneB.Columns.Add(colB);
            Column originalColumnBInstance = paneB.Columns[0];

            Program.State.Panes = new List<Pane> { paneA, paneB };

            await Program.RefreshOtherPanesAsync(paneA, dirA);

            Assert.Same(originalColumnBInstance, paneB.Columns[0]); // untouched — different directory
        }
        finally
        {
            Program.State.Panes = savedPanes;
            Directory.Delete(dirA, true);
            Directory.Delete(dirB, true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter RefreshOtherPanesAsync`
Expected: build error — `Program.RefreshOtherPanesAsync` does not exist yet.

- [ ] **Step 3: Implement `RefreshOtherPanesAsync`**

In `Program.cs`, add this method immediately after `RefreshPaneNowAsync` (currently ending at line 3577, before `RefreshActiveJobColumnsAsync`):

```csharp
    // Mirrors RefreshAffectedColumnsAsync's job-refresh logic, but for synchronous one-shot
    // operations (Rename, Create Directory) instead of background jobs. `actingPane` is skipped
    // deliberately: its active column was already replaced directly by the caller (with its own
    // RestoreTo-driven cursor placement), and refreshing it again here would clobber that.
    internal static async Task RefreshOtherPanesAsync(Pane actingPane, string affectedDir)
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

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `dotnet test --filter RefreshOtherPanesAsync`
Expected: PASS (3 tests).

- [ ] **Step 5: Wire it into `ExecuteRenameAsync`**

Replace:

```csharp
            pane.Columns[pane.ActiveColumn] = refreshed;
            await RebuildRightSideAsync(pane, pane.ActiveColumn);
        }
        else
        {
            await RefreshCurrentAsync(pane);
        }
    }
```

(this exact block appears once, inside `ExecuteRenameAsync`) with:

```csharp
            pane.Columns[pane.ActiveColumn] = refreshed;
            await RebuildRightSideAsync(pane, pane.ActiveColumn);
            await RefreshOtherPanesAsync(pane, col.Path);
        }
        else
        {
            await RefreshCurrentAsync(pane);
        }
    }
```

- [ ] **Step 6: Run the full test suite to verify no regressions**

Run: `dotnet test`
Expected: all tests PASS, same count as after Task 6 plus this task's 3 new tests.

- [ ] **Step 7: Commit**

```bash
git add Program.cs Zurari.Tests/CrossPaneRefreshTests.cs
git commit -m "fix(rename): refresh other panes showing the same directory after a rename"
```

---

## Task 8: `RequestCreateDirectory` / `ExecuteCreateDirectoryAsync`

**Files:**
- Modify: `Program.cs` (add near `ExecuteRenameAsync`, currently ending at line 4199)
- Test: `Zurari.Tests/CreateDirectoryIntegrationTests.cs` (new file)

Depends on Task 3 (validation functions), Task 4 (`ShowTextInput` extra params), Task 7 (`RefreshOtherPanesAsync`).

- [ ] **Step 1: Write the failing tests**

Create `Zurari.Tests/CreateDirectoryIntegrationTests.cs`:

```csharp
using Xunit;

public class CreateDirectoryIntegrationTests
{
    [Fact]
    public async Task ExecuteCreateDirectoryAsync_ValidName_CreatesDirAndSelectsIt()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            if (col.LoadingTask != null) await col.LoadingTask;

            var pane = new Pane();
            pane.Columns.Add(col);
            pane.ActiveColumn = 0;

            await Program.ExecuteCreateDirectoryAsync(pane, col, "NewFolder");

            Assert.True(Directory.Exists(Path.Combine(dir, "NewFolder")));
            Column refreshed = pane.Columns[0];
            Assert.Contains("NewFolder/", refreshed.Entries);
            Assert.Equal("NewFolder/", refreshed.Entries[refreshed.Selected]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExecuteCreateDirectoryAsync_CollidingName_DoesNotCreateAnother()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        Directory.CreateDirectory(Path.Combine(dir, "Existing"));
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            if (col.LoadingTask != null) await col.LoadingTask;

            var pane = new Pane();
            pane.Columns.Add(col);
            pane.ActiveColumn = 0;

            await Program.ExecuteCreateDirectoryAsync(pane, col, "Existing");

            Assert.Single(Directory.GetDirectories(dir)); // still just the original "Existing"
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExecuteCreateDirectoryAsync_EmptyName_DoesNothing()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir();
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            if (col.LoadingTask != null) await col.LoadingTask;

            var pane = new Pane();
            pane.Columns.Add(col);
            pane.ActiveColumn = 0;

            await Program.ExecuteCreateDirectoryAsync(pane, col, "   ");

            Assert.Empty(Directory.GetDirectories(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExecuteCreateDirectoryAsync_ReservedName_DoesNotCreateOnWindows()
    {
        if (!Program.IsWindows()) return;
        string dir = AsyncColumnLoadTests.MakeTempDir();
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            if (col.LoadingTask != null) await col.LoadingTask;

            var pane = new Pane();
            pane.Columns.Add(col);
            pane.ActiveColumn = 0;

            await Program.ExecuteCreateDirectoryAsync(pane, col, "CON");

            Assert.Empty(Directory.GetDirectories(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ExecuteCreateDirectoryAsync`
Expected: build error — `Program.ExecuteCreateDirectoryAsync` does not exist yet.

- [ ] **Step 3: Implement `RequestCreateDirectory` and `ExecuteCreateDirectoryAsync`**

In `Program.cs`, add these immediately after `ExecuteRenameAsync` (currently ending at line 4199):

```csharp
    static void RequestCreateDirectory(Pane pane)
    {
        Column col = pane.Columns[pane.ActiveColumn];
        if (col.Path == "::DRIVES::") return;

        ShowTextInput(
            "New Folder",
            "",
            async nameResult => await ExecuteCreateDirectoryAsync(pane, col, nameResult),
            charFilter: IsValidDirNameChar,
            hintText: "[Enter]Create  [Esc]Cancel");
    }

    // internal: Zurari.Tests から InternalsVisibleTo 経由で直接呼び出すため。
    internal static async Task ExecuteCreateDirectoryAsync(Pane pane, Column col, string? nameResult)
    {
        if (nameResult == null) return; // Esc
        string name = nameResult.Trim();
        if (name.Length == 0) return; // silent cancel — same precedent as Rename's empty-name handling

        if (IsInvalidDirectoryName(name, out string invalidReason))
        {
            _lastErrorMessage = $"\"{name}\" {invalidReason}";
            return;
        }

        if (DirectoryNameCollides(col.Entries, name))
        {
            _lastErrorMessage = $"\"{name}\" already exists";
            return;
        }

        string fullPath = BuildFullPath(col.Path, name);
        string result;
        string message = "";

        try
        {
            Directory.CreateDirectory(fullPath);
            result = "OK";
            _lastErrorMessage = null;
        }
        catch (Exception ex)
        {
            result = "FAIL";
            message = ex.Message;
            _lastErrorMessage = $"Create failed: {ex.Message}";
        }

        OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "MKDIR", result, fullPath, fullPath, message));

        if (result == "OK")
        {
            Column refreshed = await CreateColumnAsync(col.Path);
            if (refreshed.LoadingTask != null) await refreshed.LoadingTask;
            refreshed.RestoreTo = name + "/";
            SeekRestoreTo(refreshed);
            refreshed.Marked = col.Marked;
            MarkHelper.ReconcileAfterReload(refreshed);
            pane.Columns[pane.ActiveColumn] = refreshed;
            await RebuildRightSideAsync(pane, pane.ActiveColumn);
            await RefreshOtherPanesAsync(pane, col.Path);
        }
        else
        {
            await RefreshCurrentAsync(pane);
        }
    }

```

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `dotnet test --filter ExecuteCreateDirectoryAsync`
Expected: PASS (4 tests).

- [ ] **Step 5: Wire `Shift+K` into the main key switch**

In `Program.cs`, in the top-level key-dispatch switch, replace:

```csharp
            case ConsoleKey.V:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    pane.Preview.IsVisible = !pane.Preview.IsVisible;
                    UpdateHorizontalScroll(pane);
                    if (pane.Preview.IsVisible)
                        StartPreviewLoad(pane);
                    else
                        CancelPreviewLoad(pane);
                    Draw();
                }
                break;
```

With:

```csharp
            case ConsoleKey.V:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    pane.Preview.IsVisible = !pane.Preview.IsVisible;
                    UpdateHorizontalScroll(pane);
                    if (pane.Preview.IsVisible)
                        StartPreviewLoad(pane);
                    else
                        CancelPreviewLoad(pane);
                    Draw();
                }
                break;

            case ConsoleKey.K:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    RequestCreateDirectory(pane);
                break;
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 7: Run the full test suite to verify no regressions**

Run: `dotnet test`
Expected: all tests PASS, same count as after Task 7 plus this task's 4 new tests.

- [ ] **Step 8: Commit**

```bash
git add Program.cs Zurari.Tests/CreateDirectoryIntegrationTests.cs
git commit -m "feat: add Shift+K Create Directory dialog"
```

---

## Task 9: Manual verification

**No files changed in this task.** Use the `run` skill to launch the app and walk through the spec's manual verification checklist:

- [ ] **1-pane mode:** `Shift+K`, type a folder name, `Enter`. Confirm the folder is created, selected, and its (empty) child column appears to the right.
- [ ] **2-pane (upper/lower) mode:** enter split mode (`Ctrl+W`), focus each pane in turn (`Tab`), press `Shift+K`. Confirm the dialog box is vertically centered within the focused pane, not the whole screen.
- [ ] **Cross-pane refresh:** in split mode, put both panes on the same directory. Create a folder in the focused pane. Confirm the other pane's listing updates to show the new folder without manually navigating away and back.
- [ ] **Long name / scrolling:** type or paste a name wider than 50 columns (e.g. repeat a phrase). Confirm the cursor stays visible and the field scrolls correctly as you move the cursor left/right, Home, and End.
- [ ] **Japanese IME input:** switch IME on, type a Japanese folder name, confirm it composes and inserts correctly (matches Rename's existing IME behavior, reused via the same insert path).
- [ ] **Paste:** copy a long string to the clipboard, paste into the dialog (right-click or the terminal's paste shortcut). Confirm it inserts correctly.
- [ ] **Ctrl+U:** type some text, press Ctrl+U, confirm the buffer clears and the cursor returns to position 0.
- [ ] **Invalid character:** try typing `<`, `>`, `:`, `"`, `/`, `\`, `|`, `?`, `*`. Confirm each is silently rejected (buffer doesn't change).
- [ ] **Reserved name:** type `CON`, press Enter. Confirm it's rejected with a status-bar error and no folder is created.
- [ ] **Trailing space:** type `foo `, press Enter. Confirm it's rejected.
- [ ] **Name collision:** try to create a folder with a name that already exists in the current directory. Confirm the "already exists" error appears and nothing changes.
- [ ] **`::DRIVES::` guard:** navigate to the drive list (leftmost column), press `Shift+K`. Confirm nothing happens (no dialog opens).
- [ ] **Rename re-verification:** rename a file with a name longer than 50 columns — confirm the cursor/scrolling behaves the same as the new dialog. In split mode with both panes on the same directory, rename an item in one pane — confirm the other pane updates.

If any check fails, fix it and re-run the full automated suite (`dotnet test`) before re-checking.

---

## Completion Checklist

- [ ] All 8 code tasks committed with passing tests at each step (`dotnet build` clean, `dotnet test` green throughout — record the red→green counts in each commit per this project's convention).
- [ ] Task 9's manual verification checklist fully walked through in the running app.
- [ ] Spec's "Unresolved issues" — none anticipated; if anything comes up during implementation that the spec didn't anticipate, stop and record it there rather than deciding unilaterally (per `CLAUDE.md`'s Process section).
