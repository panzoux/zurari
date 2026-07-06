# Phase 6: 複数マーク 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Column` にファイル名ベースの複数マーク状態を持たせ、`Space`/`Shift+Space` での付与・解除、`*` 表示、再読込耐性、削除(`d`)の複数件対応を実装する。
**Architecture:** マーク状態は `Column.Marked`(`HashSet<string> Marked`, `StringComparer.Ordinal`)として保持し、キー入力（`HandleSpecialKeyAsync`）・描画（`DrawColumnToLines`）・操作対象組み立て（新設 `GetOperationTargets`）・再読込（`RefreshCurrentAsync` 等）の4箇所を横断的に変更する。マーク判定ロジックとターゲット組み立てロジックは `Column`/`Pane` の薄いテスト可能な静的メソッドとして切り出し、`Zurari.Tests` から直接検証する。
**Tech Stack:** C# / .NET 8, xUnit(Zurari.Tests), 自前 TUI(外部 TUI フレームワーク禁止)

---

## 前提条件(開始前に検証)

- [ ] **重要な不整合の申し送り**: 本計画は spec の「Phase 1〜5 完了後」を前提として書かれているが、計画作成時点（2026-07-06）で実際の `Program.cs`(3809行)を確認したところ、Phase 1〜5 の成果物（`Pane` クラス、`ScreenState.SplitActive`/`FocusedPane`/`Dialog`、`HandleSpecialKeyAsync(Pane pane, ...)` のようなシグネチャ変更、`DialogState` 系、`ShowConfirm`/`RecycleBin`/`OperationLog` 等）は**一切存在しない**。現状は `static class Program` が `List<Column> Columns` と `ScreenState State` を直接保持し、`HandleSpecialKeyAsync(ConsoleKeyInfo key)` は引数なしで `Columns[State.ActiveColumn]` を直接参照する形のままである（`Program.cs:1934` 以降、`Program.cs:3433` の `DrawColumnToLines` 呼び出し等）。
  - 実行セッションは着手前に `grep -n "sealed class Pane" Program.cs` と `grep -n "SplitActive" Program.cs` を実行し、Phase 1〜5 が実装済みかどうかを再確認すること。
  - **もし Phase 1〜5 が未実装のままなら**、本計画のコード断片（`Pane pane` 引数、`FocusedPane`、`ShowConfirm` 等）はそのままでは適用できない。この場合は実行セッションが「未解決事項」に「Phase 1〜5 未着手のため Phase 6 のコード断片を字面通り適用できない」旨を記録し、Phase 1〜5 の計画ファイル（`docs/superpowers/plans/2026-07-06-dual-pane-phase1-*.md` 等）の実行を先に依頼するようユーザーに報告して停止すること。自己判断で `Pane` 抽出や `ShowConfirm` を即興実装しないこと（本計画のスコープ外であり、Phase 1/3/4 の計画と重複実装すると不整合の原因になる）。
  - 本計画のコード断片は spec の API 契約（`Pane pane` 引数、`ShowConfirm(string, string, Action<bool?>)`、`OperationLog.Append`、`RecycleBin.TrySend` 等）が**すでに存在する**ことを前提に書かれている。Phase 1〜5・Phase 4（削除確認ダイアログ・ごみ箱・OperationLog）が完了している場合のみ、以下のステップをそのまま適用できる。
- [ ] `dotnet build Zurari.csproj` が exit 0 で成功する（着手前のベースライン確認）。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` が `Passed!` で全緑（既存テストが壊れていないことの確認）。
- [ ] Phase 4 完了条件の再検証: 削除機能（`d`/`Delete`）が確認ダイアログ経由でごみ箱送りとして動作し、`OperationLog` に記録されることを、`Program.cs` 内で `case ConsoleKey.Delete:` および `ShowConfirm(` 呼び出しを検索して確認する。
- [ ] Phase 5 完了条件の再検証: `Ctrl+W` による分割トグルと `Tab` によるフォーカス移動が動作し、`ScreenState.Panes`/`FocusedPane` が存在することを `grep -n "FocusedPane" Program.cs` で確認する。

この計画と spec に書かれていない判断が必要になったら、自分で決めずに「未解決事項」に記録してそのステップを保留し、次の独立したタスクに進むか停止すること。

---

### Task 1: `Column.Marked` の追加とマーク操作の純粋ロジック

**Files:**
- Modify: `Program.cs`(`sealed class Column` 定義)
- Test: `Zurari.Tests/MarkStateTests.cs`(新規作成)

- [ ] `Program.cs` の `sealed class Column` に `Marked` フィールドを追加する。既存の `Column` クラス定義（`public string Path;` から始まるフィールド群とコンストラクタ）に以下を追加する。

```csharp
sealed class Column
{
    public string Path;
    public List<string> Entries;
    public int Selected;
    public int ScrollOffset;
    public string? RestoreTo;
    public Dictionary<string, List<string>> CachedChildren;
    public DateTime CachedTime;
    public HashSet<string> Marked;

    // Loading state for async operations
    public bool IsLoading { get; set; }
    public int EntriesRead { get; set; }
    public int DirectoriesCount { get; set; }
    public CancellationTokenSource? ReadCts { get; set; }
    public Task? LoadingTask { get; set; }

    public Column(string path)
    {
        Path = path;
        Entries = new List<string>();
        Selected = 0;
        ScrollOffset = 0;
        CachedChildren = new Dictionary<string, List<string>>();
        CachedTime = DateTime.UtcNow;
        Marked = new HashSet<string>(StringComparer.Ordinal);
        IsLoading = false;
        EntriesRead = 0;
        DirectoriesCount = 0;
    }
}
```

  - 変更点は `Marked` フィールド宣言1行と、コンストラクタ内の初期化1行のみ。既存フィールドの順序・型は変更しない。

- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する（`Column` を構築している既存箇所は初期化子付きコンストラクタを通るため、ここでは壊れないはず）。

- [ ] `Program.cs` に `MarkHelper` 静的クラスを新設する。既存の `NavigationHelper` の直後（`static class NavigationHelper { ... }` の閉じ `}` の直後）に挿入する。

```csharp
static class MarkHelper
{
    // カーソル下エントリのマークをトグルし、カーソルを1つ下へ移動する。
    // ::DRIVES:: カラムでは呼び出し禁止（呼び出し側で判定する）。
    public static void ToggleMarkAndAdvance(Column column)
    {
        if (column.Entries.Count == 0) return;
        if (column.Selected < 0 || column.Selected >= column.Entries.Count) return;

        string name = column.Entries[column.Selected];
        if (column.Marked.Contains(name))
            column.Marked.Remove(name);
        else
            column.Marked.Add(name);

        if (column.Selected < column.Entries.Count - 1)
            column.Selected++;
    }

    // アクティブカラムの全マーク解除。
    public static void ClearAll(Column column)
    {
        column.Marked.Clear();
    }

    // 再読込後の Entries に存在しなくなった名前を Marked から除去する。
    // 名前ベース突合により、ソート順の変化や新規追加には影響されない。
    public static void ReconcileAfterReload(Column column)
    {
        if (column.Marked.Count == 0) return;
        var entrySet = new HashSet<string>(column.Entries, StringComparer.Ordinal);
        column.Marked.RemoveWhere(name => !entrySet.Contains(name));
    }
}
```

- [ ] `Zurari.Tests/MarkStateTests.cs` を新規作成し、`ToggleMarkAndAdvance` / `ClearAll` / `ReconcileAfterReload` を検証する（`NavigationTests.cs` と同じ `MakeColumn` パターンを踏襲）。

```csharp
using System.Collections.Generic;
using Xunit;

public class MarkStateTests
{
    static Column MakeColumn(int entryCount, int selected = 0)
    {
        var c = new Column("test");
        for (int i = 0; i < entryCount; i++)
            c.Entries.Add($"file{i:D3}.txt");
        c.Selected = selected;
        return c;
    }

    [Fact]
    public void ToggleMarkAndAdvance_MarksEntryAndMovesCursorDown()
    {
        var c = MakeColumn(5, selected: 1);
        MarkHelper.ToggleMarkAndAdvance(c);
        Assert.Contains("file001.txt", c.Marked);
        Assert.Equal(2, c.Selected);
    }

    [Fact]
    public void ToggleMarkAndAdvance_TogglingMarkedEntryUnmarksIt()
    {
        var c = MakeColumn(5, selected: 0);
        MarkHelper.ToggleMarkAndAdvance(c); // mark file000.txt, cursor -> 1
        c.Selected = 0;                     // move back
        MarkHelper.ToggleMarkAndAdvance(c); // unmark file000.txt, cursor -> 1
        Assert.DoesNotContain("file000.txt", c.Marked);
    }

    [Fact]
    public void ToggleMarkAndAdvance_AtLastEntry_DoesNotAdvancePastEnd()
    {
        var c = MakeColumn(3, selected: 2);
        MarkHelper.ToggleMarkAndAdvance(c);
        Assert.Contains("file002.txt", c.Marked);
        Assert.Equal(2, c.Selected);
    }

    [Fact]
    public void ToggleMarkAndAdvance_EmptyColumn_NoOp()
    {
        var c = MakeColumn(0);
        MarkHelper.ToggleMarkAndAdvance(c);
        Assert.Empty(c.Marked);
    }

    [Fact]
    public void ClearAll_RemovesAllMarks()
    {
        var c = MakeColumn(5);
        c.Marked.Add("file000.txt");
        c.Marked.Add("file002.txt");
        MarkHelper.ClearAll(c);
        Assert.Empty(c.Marked);
    }

    [Fact]
    public void ReconcileAfterReload_RemovesNamesNoLongerInEntries()
    {
        var c = MakeColumn(3); // file000.txt, file001.txt, file002.txt
        c.Marked.Add("file000.txt");
        c.Marked.Add("file001.txt");

        // simulate reload: file001.txt was deleted externally
        c.Entries = new List<string> { "file000.txt", "file002.txt" };
        MarkHelper.ReconcileAfterReload(c);

        Assert.Contains("file000.txt", c.Marked);
        Assert.DoesNotContain("file001.txt", c.Marked);
    }

    [Fact]
    public void ReconcileAfterReload_KeepsMarksWhenSortOrderChanges()
    {
        var c = MakeColumn(3);
        c.Marked.Add("file002.txt");

        // simulate reload: same names, different order
        c.Entries = new List<string> { "file002.txt", "file000.txt", "file001.txt" };
        MarkHelper.ReconcileAfterReload(c);

        Assert.Contains("file002.txt", c.Marked);
        Assert.Single(c.Marked);
    }

    [Fact]
    public void ReconcileAfterReload_EmptyMarked_NoOp()
    {
        var c = MakeColumn(3);
        MarkHelper.ReconcileAfterReload(c);
        Assert.Empty(c.Marked);
    }
}
```

- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、`MarkStateTests` の全ケースが `Passed!` になることを確認する（この時点では `GetOperationTargets` 等はまだ実装していないため、他のテストクラスに影響がないことも確認する）。

- [ ] コミット: `git add Program.cs Zurari.Tests/MarkStateTests.cs && git commit -m "feat: add Column.Marked and MarkHelper for multi-mark state"`

---

### Task 2: 再読込時のマーク突合を実際の再読込経路に組み込む

**Files:**
- Modify: `Program.cs`(`RefreshCurrentAsync`。分割後は Pane 対応の再読込関数も同様に変更）

- [ ] `RefreshCurrentAsync` を読む（現状 `Program.cs` の `static async Task RefreshCurrentAsync()` 内、`Column refreshed = await CreateColumnAsync(currentPath, CancellationToken.None);` の直後で `Columns[State.ActiveColumn] = refreshed;` により **Column インスタンスそのものを差し替えている**）。この差し替えが Marked 消失の発生源であるため、差し替え直前に旧 `Marked` を新インスタンスへ引き継いだ上で突合する。

```csharp
    static async Task RefreshCurrentAsync()
    {
        Column current = Columns[State.ActiveColumn];
        string currentPath = current.Path;

        SaveCursorMemory(current);

        Column refreshed = await CreateColumnAsync(currentPath, CancellationToken.None);
        if (refreshed.LoadingTask != null)
            await refreshed.LoadingTask;

        // マーク状態は Column インスタンスではなく「そのディレクトリの表示」に属するため、
        // 差し替え前に引き継いでから、新しい Entries に存在しない名前を除去する。
        refreshed.Marked = current.Marked;
        MarkHelper.ReconcileAfterReload(refreshed);

        Columns[State.ActiveColumn] = refreshed;

        await RebuildRightSideAsync(State.ActiveColumn);
    }
```

  - Phase 5 完了後は `Columns`/`State.ActiveColumn` が `pane.Columns`/`pane.ActiveColumn` に置き換わっている想定。その場合も同じ位置（`Column refreshed = await CreateColumnAsync(...)` の直後、コレクションへの代入の直前）に同じ2行（`refreshed.Marked = current.Marked;` / `MarkHelper.ReconcileAfterReload(refreshed);`）を挿入する。関数名・シグネチャは実装済みのものに合わせる。

- [ ] 同様の「`Column` インスタンス差し替え」パターンが他にないか確認する。`grep -n "= await CreateColumnAsync\|= CreateColumn(" Program.cs` を実行し、`RebuildRightSideAsync` 内で右側カラムを新規生成している箇所（`Column next = await CreateColumnAsync(nextPath, CancellationToken.None);` 等、`Program.cs:3291` 付近）を確認する。これらは**新しいディレクトリへ移動した際の新規カラム生成**であり、旧カラムのマークを引き継ぐ対象ではない（マークは「そのディレクトリの表示」に属し、移動先の別ディレクトリには無関係）。よって変更不要であることをコメントで明示する必要はないが、実行セッションは誤って全カラム生成箇所に `Marked` 引き継ぎを追加しないこと。**引き継ぎが必要なのは「同じ `Path` を指すカラムを再読込する」経路（`RefreshCurrentAsync` および F5/Ctrl+L のキー経路、将来 Phase 7 のジョブ完了時再読込）のみ**である。

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。

- [ ] 手動確認手順: アプリを起動し、あるディレクトリでいくつかのファイルに `Space` でマークを付ける（Task 3 実装後に確認可能。この時点ではビルドが通ることのみ確認し、手動確認は Task 3 完了後にまとめて行う）。

- [ ] コミット: `git add Program.cs && git commit -m "fix: preserve and reconcile Column.Marked across reload"`

---

### Task 3: Space / Shift+Space キー処理

**Files:**
- Modify: `Program.cs`(`HandleSpecialKeyAsync`)

- [ ] `HandleSpecialKeyAsync` の `switch (key.Key)` に `case ConsoleKey.Spacebar:` を追加する。挿入位置は `case ConsoleKey.F3:` の直前（キー割当表の並びに合わせる。挿入順序自体は動作に影響しない）。`::DRIVES::` カラムでは無効とする（spec「複数マーク設計」節: マークはファイル/ディレクトリの操作対象であり、ドライブ一覧に対する意味を持たない）。Shift 修飾の有無で「全解除」と「トグル+前進」を分岐する。

```csharp
            case ConsoleKey.Spacebar:
            {
                Column activeCol = Columns[State.ActiveColumn];
                if (activeCol.Path == "::DRIVES::")
                    break;

                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    MarkHelper.ClearAll(activeCol);
                }
                else
                {
                    MarkHelper.ToggleMarkAndAdvance(activeCol);
                    UpdateHorizontalScroll();
                    if (State.Preview.IsVisible) StartPreviewLoad();
                }
                break;
            }

            case ConsoleKey.F3:
                EnterLeapMode();
                break;
```

  - `UpdateHorizontalScroll()` とプレビュー再ロードは、既存の `PageUp`/`PageDown`/`Home`/`End` と同様に「カーソルが動いたら呼ぶ」という既存パターンに合わせている（`Program.cs:1993-2019` を参照）。`Shift+Space`(全解除)はカーソルを動かさないため、これらの呼び出しは不要。
  - Phase 5 完了後、`HandleSpecialKeyAsync` が `Pane pane` を引数に取る形にリファクタリングされている場合は、`Columns[State.ActiveColumn]` を `pane.Columns[pane.ActiveColumn]` に読み替えて同じロジックを挿入する。

- [ ] `ConsoleKey.Spacebar` が既存の `switch` 内の他ケースと衝突しないことを確認する: `grep -n "ConsoleKey.Spacebar" Program.cs` で重複がないことを確認する。

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。

- [ ] 手動確認手順:
  1. アプリを起動し、ファイルが3件以上あるディレクトリへ移動する。
  2. `Space` を2回押す。**期待画面**: 最初の2件が `*` プレフィックス付き・黄色（ANSI 93。Task 4 実装後に見た目が変わる。この時点では色変更が未実装のため、マーク自体は保持されているが表示は変化しない）。カーソルは3件目に移動している。
  3. `Shift+Space` を押す。**期待動作**: マークが解除される(Task 4 実装後は `*` が消えることで視認できる)。カーソル位置は変化しない。
  4. `::DRIVES::` カラム（ドライブ一覧）にカーソルがある状態で `Space` を押す。**期待動作**: 何も起きない（クラッシュしない、マークもされない）。

- [ ] コミット: `git add Program.cs && git commit -m "feat: add Space/Shift+Space key handling for marks"`

---

### Task 4: マーク表示(`*` プレフィックス + ANSI 93 / 減光時 90)

**Files:**
- Modify: `Program.cs`(`DrawColumnToLines`)

- [ ] `DrawColumnToLines` を読む。エントリ1件を描画する本体ループは2箇所ある。
  - Leap モード中の分岐（`if (inLeap && leapVisible != null) { ... }`、`Program.cs:3586` 付近）。
  - 通常/検索モードの分岐（`else { ... }`、`Program.cs:3626` 付近）。

  spec の「複数マーク設計」節は Leap/Search モード中のマーク**操作**は不可としているが、マーク済みエントリの**表示**（`*` プレフィックス）は Leap/Search モード中も維持されるべきである（マークはカラムに属する状態であり、モードによって消えるものではない）。したがって両方の分岐にマーク表示ロジックを追加する。

- [ ] 通常/検索モード分岐（`Program.cs:3629` の `for (int i = 0; i < visibleHeight; i++)` ループ内）を変更する。変更前:

```csharp
                string text = column.Entries[scrollOffset + i];
                bool isDirectory = text.EndsWith("/");
                bool isSelected = (scrollOffset + i) == column.Selected;

                int entryAbsIndex = scrollOffset + i;
                bool isCurrentMatch = inSearch && entryAbsIndex == currentMatchEntry;
                bool isOtherMatch   = inSearch && !isCurrentMatch && matchSet.Contains(entryAbsIndex);

                string prefix;
                if (isSelected)
                {
                    if (active)
                        prefix = "> ";
                    else if (isLeft)
                        prefix = "] ";
                    else
                        prefix = "  ";
                }
                else
                    prefix = "  ";

                // Truncate to fit
                string entry = CharacterWidth.SmartTruncate(text, maxEntryWidth);
```

  変更後（`isMarked` 判定を追加し、`prefix` の直後に `*` を挟む形にする。`maxEntryWidth` は Task 5 で `*` の1桁分を差し引く前提のため、ここでは `entry` のトランケート幅はそのまま `maxEntryWidth` を使う）:

```csharp
                string text = column.Entries[scrollOffset + i];
                bool isDirectory = text.EndsWith("/");
                bool isSelected = (scrollOffset + i) == column.Selected;
                bool isMarked = column.Marked.Contains(text);

                int entryAbsIndex = scrollOffset + i;
                bool isCurrentMatch = inSearch && entryAbsIndex == currentMatchEntry;
                bool isOtherMatch   = inSearch && !isCurrentMatch && matchSet.Contains(entryAbsIndex);

                string prefix;
                if (isSelected)
                {
                    if (active)
                        prefix = "> ";
                    else if (isLeft)
                        prefix = "] ";
                    else
                        prefix = "  ";
                }
                else
                    prefix = "  ";

                string markGlyph = isMarked ? "*" : "";

                // Truncate to fit (mark glyph occupies 1 column of maxEntryWidth)
                string entry = CharacterWidth.SmartTruncate(text, isMarked ? maxEntryWidth - 1 : maxEntryWidth);
```

  続けて、各描画分岐（`isCurrentMatch` / `isSelected` / `isOtherMatch` / 通常）で `entry` を使っている箇所に `markGlyph` を差し込む。マーク済みエントリの文字色は spec 通り ANSI 93（黄）とし、フォーカス時のカーソル・マッチ強調（背景色反転）とは競合しないよう「カーソル強調が最優先、`*` は残す」という spec の指示に従い、背景色付き分岐では `markGlyph` を色付けせずそのまま前置し、通常分岐でのみ `markGlyph` を ANSI 93 で色付けする。4つの分岐を以下のように変更する。

```csharp
                if (isCurrentMatch)
                {
                    // Current match: green background, black text
                    string bgColor = "\x1b[42m";
                    string textColor = "\x1b[30m";
                    string reset = "\x1b[0m";

                    string displayText = prefix + markGlyph + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);

                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    int totalDisplayWidth = displayTextWidth + paddingNeeded;

                    lines[startRow + i].AddColumn(fullLine, totalDisplayWidth, ColumnWidth);
                }
                else if (isSelected)
                {
                    // Background extends across full pane width
                    // Active: white bg, Inactive: gray bg
                    // Text: black for files, dark blue for directories
                    string bgColor = active ? "\x1b[47m" : "\x1b[100m";
                    string textColor = isDirectory ? "\x1b[94m" : "\x1b[30m";
                    string reset = "\x1b[0m";

                    string displayText = prefix + markGlyph + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);

                    // Build full line: background + text color + content + padding + reset
                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    int totalDisplayWidth = displayTextWidth + paddingNeeded;

                    lines[startRow + i].AddColumn(fullLine, totalDisplayWidth, ColumnWidth);
                }
                else if (isOtherMatch)
                {
                    // Other match: green foreground text on default background
                    int displayWidth = 2 + (isMarked ? 1 : 0) + CharacterWidth.GetStringWidth(entry);
                    string coloredEntry = "\x1b[32m" + entry + "\x1b[0m";
                    string coloredMark = isMarked ? "\x1b[93m*\x1b[0m" : "";
                    lines[startRow + i].AddColumn(prefix + coloredMark + coloredEntry, displayWidth, ColumnWidth);
                }
                else
                {
                    // Non-selected: use original logic
                    int displayWidth = 2 + (isMarked ? 1 : 0) + CharacterWidth.GetStringWidth(entry);
                    if (isDirectory)
                        entry = AnsiColors.Colorize(entry, AnsiColors.Blue);
                    string coloredMark = isMarked ? "\x1b[93m*\x1b[0m" : "";
                    lines[startRow + i].AddColumn(prefix + coloredMark + entry, displayWidth, ColumnWidth);
                }
```

  - `isCurrentMatch`/`isSelected` の背景色分岐では `markGlyph`(色なし生文字)をそのまま `displayText` に含める。背景色・文字色は行全体に一括適用されるため、`*` だけを別色にすると `\x1b[0m` リセットが背景色を巻き込み崩れる。spec の「カーソル強調を優先しつつ `*` は残す」はこの「背景色分岐では無色の `*` を残す」という解釈で満たす。
  - `isOtherMatch`/通常分岐では `*` を独立して ANSI 93 で色付けする（`\x1b[93m*\x1b[0m`）。非フォーカス時の減光は次のステップで扱う。

- [ ] **非フォーカスペインの減光規則**: spec は Phase 5 で「非フォーカスペインは減光パレット（ANSI 90 系）」を導入済みである前提。`DrawColumnToLines` の呼び出し元（`BuildFrame` または Phase 5 で導入されたペイン描画ラッパー）が「どちらのパレットを使うか」を bool 引数として渡している場合、その bool（例: `bool dimmed`）を `DrawColumnToLines` のシグネチャに追加で受け取り、マーク色を次のように出し分ける。

```csharp
                string coloredMark = isMarked ? (dimmed ? "\x1b[90m*\x1b[0m" : "\x1b[93m*\x1b[0m") : "";
```

  上記の3箇所（`isOtherMatch` 分岐、通常分岐）の `coloredMark` 行をこの出し分け版に置き換える。`dimmed` 引数の実際の名前・受け渡し経路は Phase 5 の実装（`DrawColumnToLines` の既存シグネチャに減光 bool がすでに追加されているはず）に合わせる。**Phase 5 の実装を確認せずに新しい bool 引数を独自に追加しないこと** — 既存のシグネチャ変更箇所を `grep -n "DrawColumnToLines" Program.cs` で確認し、既にある減光 bool 引数名をそのまま使うこと。もし Phase 5 が減光 bool を渡していない場合（前提条件チェックで Phase 5 未完了が判明した場合と同様）、このステップは保留して「未解決事項」に記録する。

- [ ] Leap モード分岐（`Program.cs:3586` 付近の `if (inLeap && leapVisible != null) { ... }`）にも同様の `*` 表示を追加する。変更前:

```csharp
                int absIdx = leapVisible[vi];
                string text = column.Entries[absIdx];
                bool isDirectory = text.EndsWith("/");
                bool isSelected = absIdx == column.Selected;

                string prefix = (isSelected && active) ? "> " : "  ";
                string entry = CharacterWidth.SmartTruncate(text, maxEntryWidth);

                if (isSelected && active)
                {
                    string bgColor = "\x1b[47m";
                    string textColor = isDirectory ? "\x1b[94m" : "\x1b[30m";
                    string reset = "\x1b[0m";
                    string displayText = prefix + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);
                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    lines[startRow + i].AddColumn(fullLine, displayTextWidth + paddingNeeded, ColumnWidth);
                }
                else
                {
                    int displayWidth = 2 + CharacterWidth.GetStringWidth(entry);
                    if (isDirectory)
                        entry = AnsiColors.Colorize(entry, AnsiColors.Blue);
                    lines[startRow + i].AddColumn(prefix + entry, displayWidth, ColumnWidth);
                }
```

  変更後:

```csharp
                int absIdx = leapVisible[vi];
                string text = column.Entries[absIdx];
                bool isDirectory = text.EndsWith("/");
                bool isSelected = absIdx == column.Selected;
                bool isMarked = column.Marked.Contains(text);

                string prefix = (isSelected && active) ? "> " : "  ";
                string markGlyph = isMarked ? "*" : "";
                string entry = CharacterWidth.SmartTruncate(text, isMarked ? maxEntryWidth - 1 : maxEntryWidth);

                if (isSelected && active)
                {
                    string bgColor = "\x1b[47m";
                    string textColor = isDirectory ? "\x1b[94m" : "\x1b[30m";
                    string reset = "\x1b[0m";
                    string displayText = prefix + markGlyph + entry;
                    int displayTextWidth = CharacterWidth.GetStringWidth(displayText);
                    int paddingNeeded = Math.Max(0, contentSlot - displayTextWidth);
                    string fullLine = bgColor + textColor + displayText + new string(' ', paddingNeeded) + reset;
                    lines[startRow + i].AddColumn(fullLine, displayTextWidth + paddingNeeded, ColumnWidth);
                }
                else
                {
                    int displayWidth = 2 + (isMarked ? 1 : 0) + CharacterWidth.GetStringWidth(entry);
                    if (isDirectory)
                        entry = AnsiColors.Colorize(entry, AnsiColors.Blue);
                    string coloredMark = isMarked ? "\x1b[93m*\x1b[0m" : "";
                    lines[startRow + i].AddColumn(prefix + coloredMark + entry, displayWidth, ColumnWidth);
                }
```

  - Leap モードの非フォーカスペインでの減光出し分けも、通常モードと同じ `dimmed` 引数（Phase 5 実装済みの引数名）を使って `coloredMark` を出し分ける。Phase 5 未完了の場合は同様に保留する。

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。

- [ ] 手動確認手順:
  1. ファイルが5件程度あるディレクトリで `Space` を2回押す。**期待画面**: 1件目・2件目の行頭に `*` が黄色（ANSI 93）で表示され、カーソルは3件目にある。
  2. マーク済みエントリにカーソルを重ねる（矢印キーでカーソルをマーク済み行に戻す）。**期待画面**: カーソル背景色（白 or グレー背景）は通常通り表示され、`*` は背景色の上にそのまま残る（色は反転してよい。spec の要求は「`*` が残ること」であり色の可読性は背景色分岐の既存ルールに従う）。
  3. `Ctrl+W` で分割し、非フォーカスペイン側でマークを事前に付けておく（フォーカス移動前にマークしておく）。**期待画面**: 非フォーカスペインのマーク `*` はグレー系（ANSI 90）で表示される。

- [ ] コミット: `git add Program.cs && git commit -m "feat: render mark glyph with ANSI 93/90 in DrawColumnToLines"`

---

### Task 5: 幅計算の調整(`*` 1桁分)

**Files:**
- Modify: `Program.cs`(`DrawColumnToLines` 冒頭の `maxEntryWidth` 計算箇所)

- [ ] Task 4 の変更で、`entry` のトランケート幅はすでに `isMarked ? maxEntryWidth - 1 : maxEntryWidth` としてエントリ単位で調整済みである。これは spec の「名前の表示幅計算では `*` の1桁分を差し引く」を満たす最小限の変更であり、`maxEntryWidth` 自体（`Program.cs:3543` 付近、`int maxEntryWidth = contentSlot - 2;` — 全エントリ共通の「2文字プレフィックス分」を差し引く既存の計算）はマーク有無に関わらず一定のプレフィックス幅（`"> "` 等の2文字)を前提にしているため変更しない。
  - 確認: `maxEntryWidth` を変更すると、マークなしエントリの表示幅まで狭まってしまい、マークの有無で全エントリのレイアウトが変わってしまう（spec が意図する挙動ではない）。したがって「共通の `maxEntryWidth` からマーク時のみ追加で1桁引く」という Task 4 の方式が正しい。このタスクは Task 4 の実装が正しく行われていることの確認のみで、追加のコード変更はない。
  - `grep -n "int maxEntryWidth = contentSlot - 2;" Program.cs` を実行し、1箇所のみ存在すること（Leap 分岐・通常分岐で共有されていること）を確認する。

- [ ] 幅計算の境界値確認（手動）: `ColumnWidth`(32) 相当の狭いカラムで、ファイル名がちょうど `maxEntryWidth` 文字（全角なし）のエントリにマークを付ける。**期待画面**: マーク時は末尾が1文字分省略される（`SmartTruncate` の `…` 省略記号が現れるか、境界ちょうどなら1文字減る）。マークなし時と比べて行の右端がはみ出さないこと（列崩れがないこと）を目視確認する。

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。

- [ ] コミット不要（Task 4 に含まれる。このタスクは検証のみのため、コード変更があった場合のみ `git add Program.cs && git commit -m "fix: adjust maxEntryWidth accounting for mark glyph"` を実行する）。

---

### Task 6: `GetOperationTargets` の新設と削除(`d`)の複数件対応

**Files:**
- Modify: `Program.cs`(削除処理箇所。`ShowConfirm` を呼んでいる `case ConsoleKey.Delete:` 相当のハンドラ、および削除実行関数)
- Test: `Zurari.Tests/MarkStateTests.cs`(追記)

- [ ] 削除処理の現状箇所を確認する: `grep -n "ConsoleKey.Delete\|case ConsoleKey.D:" Program.cs` および `grep -n "ShowConfirm" Program.cs` で、Phase 4 実装済みの削除ハンドラの正確な関数名・呼び出し形を特定する。以降のコード例は spec の API 契約（`ShowConfirm(string title, string message, Action<bool?> onResult)`、`RecycleBin.TrySend(string path, out string error)`、`OperationLog.Append(OpLogEntry)`）に基づく想定コードであり、実際の変数名・関数名は Phase 4 実装済みコードに合わせて読み替えること。

- [ ] `static List<string> GetOperationTargets(Pane pane)` を新設する。挿入位置は `MarkHelper` 静的クラスの直後。

```csharp
    // 操作対象の組み立て規則(spec「複数マーク設計」節):
    // フォーカスペインのアクティブカラムに1件以上マークがあればマーク集合(フルパス化)、
    // なければカーソル下1件。他カラム・他ペインのマークは常に無視する。
    // internal: Zurari.Tests から InternalsVisibleTo 経由で直接呼び出すため。
    internal static List<string> GetOperationTargets(Pane pane)
    {
        Column active = pane.Columns[pane.ActiveColumn];
        var result = new List<string>();

        if (active.Marked.Count > 0)
        {
            // Entries の表示順を保つため、Marked（順序無保証の HashSet）ではなく
            // Entries を走査して Marked に含まれるものだけを拾う。
            foreach (string name in active.Entries)
            {
                if (active.Marked.Contains(name))
                    result.Add(BuildFullPath(active.Path, name));
            }
            return result;
        }

        if (active.Entries.Count > 0 && active.Selected >= 0 && active.Selected < active.Entries.Count)
        {
            result.Add(BuildFullPath(active.Path, active.Entries[active.Selected]));
        }

        return result;
    }
```

  - `BuildFullPath(string columnPath, string entryName)` という名前のフルパス組み立てヘルパが既存コードに存在するか `grep -n "BuildFullPath\|GetCurrentFullPath\|Path.Combine" Program.cs` で確認する。既存の同等ロジック（例えば現在の削除処理内でカーソル下1件のフルパスを組み立てている式）をそのまま関数化するか、既存の private ヘルパ名に置き換えること。エントリ名の末尾 `/` サフィックス（ディレクトリ）の扱いも既存ロジックに合わせ、新規に別ルールを作らない。
  - `Pane` 型が Phase 1 で導入されている前提（`pane.Columns`, `pane.ActiveColumn`）。Phase 1 未完了の場合は「未解決事項」に記録し、`static List<string> GetOperationTargets()`(引数なし、`Columns`/`State.ActiveColumn` を直接参照する版)への読み替えが必要である旨を記す。

- [ ] 既存の削除ハンドラを `GetOperationTargets(pane)` 経由に変更する。確認ダイアログのメッセージに対象件数を表示する。想定コード（Phase 4 実装済みの削除ハンドラを次のように置き換える。実際の変数名は現行コードに合わせる）:

```csharp
            case ConsoleKey.Delete:
            {
                List<string> targets = GetOperationTargets(FocusedPane);
                if (targets.Count == 0) break;

                string message = targets.Count == 1
                    ? $"「{Path.GetFileName(targets[0].TrimEnd('/'))}」を削除しますか?(ごみ箱へ)"
                    : $"{targets.Count}件を削除しますか?(ごみ箱へ)";

                ShowConfirm("削除の確認", message, async confirmed =>
                {
                    if (confirmed != true) return;

                    Column activeCol = FocusedPane.Columns[FocusedPane.ActiveColumn];
                    int okCount = 0, failCount = 0;
                    string? firstError = null;

                    foreach (string target in targets)
                    {
                        bool ok = RecycleBin.TrySend(target, out string error);
                        // Marked のキーは Entries の文字列と完全一致(ディレクトリは "/" サフィックス付き)。
                        // フルパスの末尾から同じ形式のエントリ名を復元する。
                        bool isDir = target.EndsWith("/");
                        string entryName = Path.GetFileName(target.TrimEnd('/')) + (isDir ? "/" : "");

                        OperationLog.Append(new OpLogEntry(
                            DateTime.UtcNow, null, "DELETE",
                            ok ? "OK" : "FAIL",
                            target, "", ok ? "" : error));

                        if (ok)
                        {
                            okCount++;
                            activeCol.Marked.Remove(entryName);
                        }
                        else
                        {
                            failCount++;
                            firstError ??= error;
                        }
                    }

                    _lastErrorMessage = failCount == 0
                        ? $"削除完了: {okCount}件"
                        : $"削除: {okCount}件完了 / {failCount}件失敗: {firstError}";

                    RefreshCurrent();
                });
                break;
            }
```

  - 「成功したエントリのマークは解除する。スキップ・失敗したエントリのマークは維持する」という spec の実行後ルールを、成功時のみ `activeCol.Marked.Remove(...)` する形で満たす。
  - 単一件（マークなし・カーソル下1件）のときはファイル名を明示したメッセージ、複数件（マークあり）のときは件数のみのメッセージという spec 通りの文言分岐にする。
  - `d` キー（文字キー）で同じハンドラを呼んでいる既存コードがあれば同様に置き換える。`grep -n "'d'\|KeyChar == 'd'" Program.cs` で確認する。

- [ ] `GetOperationTargets` のユニットテストを `Zurari.Tests/MarkStateTests.cs` に追記する。`Pane` 型が存在する前提でテストを書く。`GetOperationTargets` は `internal static` のため、`InternalsVisibleTo("Zurari.Tests")` 経由で `Program.GetOperationTargets(pane)` を直接呼び出せる。

```csharp
    static Pane MakePane(Column column)
    {
        var p = new Pane();
        p.Columns.Add(column);
        p.ActiveColumn = 0;
        return p;
    }

    [Fact]
    public void GetOperationTargets_NoMarks_ReturnsCursorEntryOnly()
    {
        var c = MakeColumn(5, selected: 2);
        c.Path = @"C:\dir";
        var pane = MakePane(c);

        var targets = Program.GetOperationTargets(pane);

        Assert.Single(targets);
        Assert.EndsWith("file002.txt", targets[0]);
    }

    [Fact]
    public void GetOperationTargets_WithMarks_ReturnsMarkedEntriesInEntryOrder()
    {
        var c = MakeColumn(5, selected: 0);
        c.Path = @"C:\dir";
        c.Marked.Add("file003.txt");
        c.Marked.Add("file001.txt");
        var pane = MakePane(c);

        var targets = Program.GetOperationTargets(pane);

        Assert.Equal(2, targets.Count);
        Assert.EndsWith("file001.txt", targets[0]); // Entries順: file001 が file003 より先
        Assert.EndsWith("file003.txt", targets[1]);
    }

    [Fact]
    public void GetOperationTargets_EmptyColumn_ReturnsEmpty()
    {
        var c = MakeColumn(0);
        var pane = MakePane(c);

        var targets = Program.GetOperationTargets(pane);

        Assert.Empty(targets);
    }
```

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` で全緑を確認する。

- [ ] 手動確認手順:
  1. ファイル3件をマークし、`d` を押す。**期待画面**: 確認ダイアログに「3件を削除しますか?(ごみ箱へ)」と表示される。
  2. `Y` で確定する。**期待動作**: 3件がごみ箱へ送られ、カラムが再読込され、ステータス行に「削除完了: 3件」が表示される。
  3. マークなしでカーソルを1件のファイルに合わせ `d` を押す。**期待画面**: 確認ダイアログに「「<ファイル名>」を削除しますか?(ごみ箱へ)」と表示される（件数表示ではなくファイル名表示）。

- [ ] コミット: `git add Program.cs Zurari.Tests/MarkStateTests.cs && git commit -m "feat: route delete through GetOperationTargets with count-aware confirmation"`

---

### Task 7: ステータス行「M件マーク中」の追記

**Files:**
- Modify: `Program.cs`(`BuildFrame` の通常ヒント分岐)

- [ ] `BuildFrame` 内、ステータス行を組み立てる `else`（Leap でも Search でも `_lastErrorMessage` でもない、通常ヒント）分岐（`Program.cs:3498` 付近）を変更する。変更前:

```csharp
        else
        {
            string previewHint = State.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
            string status;
            if (IsWindows())
                status = $"Esc=Quit | Ctrl+Enter=Open | Shift+Enter/\\=Menu | {previewHint}";
            else
                status = $"Esc=Quit | Ctrl+Enter=Open File | {previewHint}";
            status = CharacterWidth.SmartTruncate(status, width);
            frame[height - 1] = CharacterWidth.PadToWidth(status, width);
        }
```

  変更後(**「通常ヒント時のみ」**の追記のため、Leap/Search/エラー/ジョブサマリの各分岐には手を加えない。フォーカスペインのアクティブカラムのマーク件数を参照する):

```csharp
        else
        {
            string previewHint = State.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
            string status;
            if (IsWindows())
                status = $"Esc=Quit | Ctrl+Enter=Open | Shift+Enter/\\=Menu | {previewHint}";
            else
                status = $"Esc=Quit | Ctrl+Enter=Open File | {previewHint}";

            int markedCount = Columns[State.ActiveColumn].Marked.Count;
            if (markedCount > 0)
                status += $" | {markedCount}件マーク中";

            status = CharacterWidth.SmartTruncate(status, width);
            frame[height - 1] = CharacterWidth.PadToWidth(status, width);
        }
```

  - Phase 5 完了後は `Columns[State.ActiveColumn]` を `FocusedPane.Columns[FocusedPane.ActiveColumn]` に読み替える。
  - `CharacterWidth.SmartTruncate` が末尾で切り詰めるため、マーク件数表示が幅超過時に省略されても崩れない（既存の省略ロジックをそのまま使う）。

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。

- [ ] 手動確認手順:
  1. マークなしの状態でステータス行を確認する。**期待画面**: 「M件マーク中」は表示されない。
  2. 2件マークする。**期待画面**: ステータス行末尾に「| 2件マーク中」が追記される。
  3. `Shift+Space` で全解除する。**期待画面**: 「M件マーク中」が消える。
  4. Leap モード中・Search モード中はマークがあってもこの追記が出ない（Leap バー/Search バーがステータス行を占有するため）ことを確認する。

- [ ] コミット: `git add Program.cs && git commit -m "feat: show marked count in normal status hint"`

---

## 完了条件チェックリスト

- [ ] `Space` でカーソル下エントリのマークがトグルされ、カーソルが1つ下へ移動する。
- [ ] `Shift+Space` でアクティブカラムの全マークが解除される。
- [ ] `::DRIVES::` カラムでは `Space`/`Shift+Space` が無効（クラッシュ・誤動作なし）。
- [ ] マーク済みエントリは `*` プレフィックス+ANSI 93(フォーカス時)/ANSI 90(非フォーカス時)で表示される。
- [ ] マーク時の名前表示幅が `*` の1桁分正しく縮小され、列崩れがない。
- [ ] カラム再読込（`RefreshCurrentAsync` 経由）後も、`Entries` に残っている名前のマークは維持され、消えた名前のマークのみ除去される。
- [ ] `GetOperationTargets` が「マークがあればマーク集合（`Entries` 順・フルパス）、なければカーソル下1件」を返す。
- [ ] 削除(`d`/`Delete`)が `GetOperationTargets` 経由になり、複数件時は「N件を削除しますか?(ごみ箱へ)」、単一件時はファイル名表示の確認ダイアログを経て動作する。
- [ ] 削除成功後、成功したエントリのマークのみ解除される（失敗分は維持）。
- [ ] ステータス行の通常ヒント時のみ「M件マーク中」が追記され、Leap/Search/エラー表示中には出ない。
- [ ] `MarkStateTests.cs` の全ケース（トグル、全解除、再読込突合、`GetOperationTargets` の規則）が `dotnet test` で緑。
- [ ] 既存の全ユニットテストが緑のまま。
- [ ] `dotnet build Zurari.csproj` が exit 0。

## 未解決事項

(実行セッションが判断保留を記録する欄。空のまま残すこと)
