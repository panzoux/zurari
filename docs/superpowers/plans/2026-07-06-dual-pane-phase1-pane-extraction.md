# Phase 1: Pane 抽出リファクタ 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Program` の static グローバル(`Columns` / `State.ActiveColumn` / `State.HorizontalScroll` / `State.Search` / `State.Leap` / `State.Preview`)を新設 `Pane` クラスに束ね、これらを参照する全 static メソッドのシグネチャに先頭引数 `Pane pane` を追加し、見た目・挙動を一切変えずに実行する。
**Architecture:** `Pane` クラスに1画面分のナビゲーション状態を集約し、`ScreenState` はフレームバッファと `List<Pane> Panes`(単一ペイン運用では常に1要素)のみを保持する縮小形にする。`Program` の全ナビゲーション/Enter/検索/Leap 関連 static メソッドは `Pane pane` を先頭引数として受け取る形にリファクタリングし、呼び出し元は `FocusedPane`(`State.Panes[State.FocusedPane]`)を渡す。描画系(`Draw`/`BuildFrame`/`DrawColumnToLines`/`DrawPreviewToLines`)は Phase 2 まで現状の `State` 直接参照のままとし、本 Phase では変更しない。
**Tech Stack:** C# / .NET 8, xUnit(Zurari.Tests), 自前 TUI(外部 TUI フレームワーク禁止)

---

## 前提条件(開始前に検証)
- [ ] `git log --oneline -1` を実行し、直近コミットが存在すること(現時点の HEAD: `928735a docs: add ConflictDecision record to carry rename target name to engine`)。本計画は Phase 0(コピー/移動エンジンの設計確定)完了後の最初の実装 Phase であるため、直前 Phase の完了条件は存在しない。代わりに現状のビルド・テストが緑であることを確認する。
- [ ] `dotnet build Zurari.csproj` を実行し、exit code 0 で成功すること。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、`合格: 53、失敗: 0`(または現在の全件)で緑であること。

この計画と spec(`docs/superpowers/specs/2026-07-06-dual-pane-copy-move-design.md`)に書かれていない判断が必要になったら、自分で決めずに本ファイル末尾の「未解決事項」に記録してそのステップを保留し、次の独立したタスクに進むか停止すること。

---

## 対象メソッド完全一覧(Program.cs 精読結果)

`Program.cs`(約3800行)を全文精読し、`Columns` / `State.ActiveColumn` / `State.HorizontalScroll` / `State.Search` / `State.Leap` / `State.Preview` のいずれかを参照する `static class Program` 内の全メソッドを機能クラスタごとに列挙する。行番号は変化するため、メソッド名で特定すること。

### クラスタA: ナビゲーション(Task 2)
- `SelectedItemIsFileOrDrive()`
- `GetPreviewTarget()`
- `MoveUpAsync()`
- `MoveDownAsync()`
- `CancelRightSideReads(int columnIndex)`
- `ValidateAndRebuildRightPaneIfNeeded(int columnIndex)`
- `MoveLeft()`
- `MoveRightAsync()`
- `UpdateHorizontalScroll()`
- `Parent()`
- `RefreshCurrent()`
- `RefreshCurrentAsync()`
- `GetCurrentFullPath()`
- `RebuildRightSideAsync(int columnIndex)`
- `GetSelectedDirectory(int columnIndex)`
- `HandleResizeIfNeeded()`

### クラスタB: Enter系(Task 3)
- `EnterAsync()`
- `OpenFileAsync()`
- `ShowContextMenuAsync()`

### クラスタC: プレビュー(Task 4)
- `StartPreviewLoad(bool forceReload = false)`
- `CancelPreviewLoad()`

### クラスタD: 検索(Task 5)
- `RecomputeMatchesAsync()`
- `EnterSearchMode()`
- `ExitSearchMode(bool restoreCursor)`
- `HandleSearchKeyAsync(ConsoleKeyInfo key)`

### クラスタE: Leap(Task 6)
- `EnterLeapMode()`
- `ExitLeapModeAsync(bool restoreCursor)`
- `HandleLeapKeyAsync(ConsoleKeyInfo key)`
- `EnterDirInLeapAsync()`
- `TryLeapAutoEnterAsync()`
- `LeapStepUpAsync()`
- `GoLeapRootAsync()`
- `MoveLeapCursorDown()`
- `MoveLeapCursorUp()`
- `ApplyLeapFilter()`

### クラスタF: キー分岐 + メインループ(Task 7)
- `HandleSpecialKeyAsync(ConsoleKeyInfo key)`
- `Main()`(ループ内で `Columns` / `State.ActiveColumn` / `State.Leap` / `State.Search` / `State.Preview` を直接参照している箇所を `FocusedPane` 経由に置換)

### 変更しないメソッド(Phase 2 まで据え置き — spec の描画設計節に基づく例外)
- `Draw()`, `BuildFrame(int width, int height)`, `DrawColumnToLines(...)`, `DrawPreviewToLines(...)`, `WriteLineAt(...)`
  理由: spec は「`BuildFrame` を3層構成に分解する」作業を明示的に Phase 2 に割り当てている。Phase 1 は状態の入れ物(`Pane`)を作るだけで、描画がその状態をどう読むかは変えない。`BuildFrame`/`DrawColumnToLines` は `State.Leap`/`State.Search`/`State.Preview`/`Columns`/`State.ActiveColumn`/`State.HorizontalScroll` を読むが、Phase 1 では `State.Panes[State.FocusedPane]` の中身を指す**プロパティ経由の参照**に置き換えるに留め、引数構造(`Pane pane` を取る形)には変更しない。具体的には Task 7 で `Columns` → `FocusedPane.Columns`、`State.ActiveColumn` → `FocusedPane.ActiveColumn` 等の**読み替えのみ**行い、シグネチャは変えない。
- テストコード(`Zurari.Tests/*.cs`)は `Program` の static メソッドを直接参照していない(`Column`, `NavigationHelper`, `SearchHelper`, `FileTypeDetector` 等の独立クラスのみをテスト対象にしている)ため、Phase 1 のシグネチャ変更によるテスト修正は不要と確認済み。

---

## 変換規則(全タスク共通・曖昧さゼロ)

1. メソッド本体中の `Columns` への参照 → `pane.Columns` に置換。
2. `State.ActiveColumn` → `pane.ActiveColumn` に置換。
3. `State.HorizontalScroll` → `pane.HorizontalScroll` に置換。
4. `State.Search` → `pane.Search` に置換。
5. `State.Leap` → `pane.Leap` に置換。
6. `State.Preview` → `pane.Preview` に置換。
7. 対象メソッドのシグネチャ先頭に `Pane pane` 引数を追加する(例: `static async Task MoveUpAsync()` → `static async Task MoveUpAsync(Pane pane)`)。既存の引数がある場合は `pane` を一番先頭に置き、既存引数は順序維持のまま後ろにずらす。
8. 対象メソッドを呼び出している箇所(`Program` 内の他メソッド、`Main`)は、呼び出し元がすでに `pane` を受け取っていれば `MoveUpAsync(pane)` のように同じ `pane` をそのまま渡す。呼び出し元がまだ `pane` を受け取っていない(=このタスクでまだシグネチャ変更していない)場合は、暫定的に `FocusedPane`(Task 1 で定義)を渡す。すべてのクラスタが完了した後の最終形では、呼び出し連鎖の起点は `Main` の `FocusedPane` 参照のみになる。
9. `lock (_searchLock) { State.Search.XXX }` のような、共有ロック配下で `State.Search` を読む箇所は、ロック自体(`_searchLock`)はそのまま共有 static として残し、ロック**内側**の `State.Search` 参照のみ `pane.Search` に置換する(ロック機構自体はペイン化しない。Phase 1 でも `_searchLock` は単一の共有 lock のまま — 複数ペイン下での lock 粒度の見直しは Phase 5 以降のスコープであり、本計画では扱わない。判断が必要になった場合は「未解決事項」へ記録)。
10. `_migemo`, `_lastErrorMessage`, `_cursorMemory`, `_cursorMemoryKeys`, `_lastNavigationTime`, `_searchLock` は spec 通り共有 static のまま変更しない(`pane.` へ置換しない)。
11. `Draw()` / `BuildFrame` / `DrawColumnToLines` / `DrawPreviewToLines` は、シグネチャに `Pane pane` を追加**しない**。その内部の `Columns` / `State.ActiveColumn` / `State.HorizontalScroll` / `State.Search` / `State.Leap` / `State.Preview` は `FocusedPane.Columns` / `FocusedPane.ActiveColumn` 等に読み替える(`FocusedPane` は `State.Panes[State.FocusedPane]` を返す static ヘルパ。Task 1 で定義)。これは変換規則1〜6の「置換先が `pane.X` ではなく `FocusedPane.X` になる」という例外である。

---

### Task 1: Pane クラス新設 + ScreenState 縮小 + FocusedPane ヘルパ

**Files:**
- Modify: `Program.cs`

- [ ] `Program.cs` 内の `sealed class ScreenState { ... }` 定義(`public int ActiveColumn;` から始まるブロック)を次のように置き換える。

変更前:
```csharp
sealed class ScreenState
{
    public int ActiveColumn;
    public int HorizontalScroll;
    public string[]? PrevFrame;
    public int PrevWidth;
    public int PrevHeight;
    public readonly PreviewPane Preview = new();
    public SearchState Search = new();
    public LeapState Leap = new();
}
```

変更後:
```csharp
sealed class Pane
{
    public List<Column> Columns = new();
    public int ActiveColumn;
    public int HorizontalScroll;
    public SearchState Search = new();
    public LeapState Leap = new();
    public PreviewPane Preview = new();
    public bool NeedsRefresh; // ジョブ完了時の遅延再読込用 dirty フラグ(Phase 7 で使用)
}

sealed class ScreenState
{
    public string[]? PrevFrame;
    public int PrevWidth;
    public int PrevHeight;
    public List<Pane> Panes = new();   // 常に1〜2要素
    public int FocusedPane;            // 0 = 上(または単一), 1 = 下
    public bool SplitActive;
    public DialogState? Dialog;        // Phase 3 で追加。今は宣言のみ。
}
```

- [ ] 同じ場所(`ScreenState` 定義の直後)に、Phase 3 で実体を追加する `DialogState` の仮の空定義を追加する(コンパイルを通すためのプレースホルダではなく、spec API契約通りの正式な抽象クラス — 中身が空なのは Phase 3 で肉付けされる設計上の事実であり省略ではない)。
```csharp
abstract class DialogState
{
    public string Title = "";
}
```

- [ ] `static class Program` の先頭付近、`static readonly List<Column> Columns = new List<Column>();` の行を削除し、`static readonly ScreenState State = new ScreenState();` の直後に `FocusedPane` ヘルパを追加する。

変更前:
```csharp
    static readonly List<Column> Columns = new List<Column>();
    static readonly ScreenState State = new ScreenState();
    static DateTime _lastNavigationTime = DateTime.MinValue;
```

変更後:
```csharp
    static readonly ScreenState State = new ScreenState();
    static Pane FocusedPane => State.Panes[State.FocusedPane];
    static DateTime _lastNavigationTime = DateTime.MinValue;
```

- [ ] `Main()` 内、`Columns.Add(CreateColumn(root));` の直前で `Pane` を1つ生成して `State.Panes` に登録する処理を追加する。

変更前:
```csharp
        try
        {
            string root = GetRootPath();
            Columns.Add(CreateColumn(root));
            await RebuildRightSideAsync(0);
```

変更後:
```csharp
        try
        {
            string root = GetRootPath();
            State.Panes.Add(new Pane());
            State.FocusedPane = 0;
            FocusedPane.Columns.Add(CreateColumn(root));
            await RebuildRightSideAsync(0);
```

  (`RebuildRightSideAsync` の呼び出しはこの時点ではまだ旧シグネチャ `RebuildRightSideAsync(int columnIndex)` のままでよい。`Columns` を暗黙参照している内部実装は Task 2 で `pane.Columns` に切り替わる。このタスクの時点では `RebuildRightSideAsync` 内部はまだ `Columns`(削除済み)を参照しており**ビルドが通らない状態が一時的に発生する**。これは意図的であり、次のステップで解消する。)

- [ ] この時点でビルドを試し、失敗することを確認する(`Columns` の削除により、他の全メソッドがコンパイルエラーになるはず)。これは Task 1 単体では検証可能な状態にならないことの確認であり、次点の「中間状態の扱い」に記載の通りタスクをまとめて進める根拠になる。
  ```
  dotnet build Zurari.csproj
  ```
  期待結果: exit code 非0、`Columns` が見つからない旨のエラーが多数出る。

**中間状態でもビルドが通るタスク分割にするか否かの判断**: `Columns` は `static class Program` 内の非常に多くのメソッドから参照されており、フィールド定義を削除した瞬間に他の全メソッドがコンパイルエラーになる。「1メソッドずつ直して都度ビルドを通す」という粒度にすると、削除直後の1タスク内では必ずビルドが壊れる中間状態を経由する。これを避けるには「`Columns` を削除しない(残したまま `Pane.Columns` を追加し、両方が一時的に共存する)」という代替も検討したが、spec のAPI契約は `Columns` の完全な `pane.Columns` への置換を要求しており、共存させると「どちらが真の状態か」が曖昧になり事故の元になる。よって本計画では **Task 1 で `Columns` を削除してから Task 2〜7 で全クラスタを順に直す一連の流れを「1つの大きな移行」として扱い、Task 1〜7 の全step完了までは `dotnet build` が失敗し続けることを許容する**。Task 7 完了時点で初めてビルドが通る。各タスクの粒度は「1クラスタ = 1コミット」を保ちつつ、コミットは「ビルドが通る単位」ではなく「意味のある変更のまとまり」を基準にする。この判断の代わりに全クラスタをTask1内の1コミットにまとめる案もあったが、レビュー可能性(1クラスタ=数メソッド程度への差分の局所化)を優先し、複数コミットに分ける。**注意**: Task 2〜6 の途中では `dotnet build` も `dotnet test` も実行しない(失敗するとわかっているため無意味)。Task 7 完了後に初めてビルド・テストを実行する。Task 1〜6 の各ステップはコード変更のみを行い、コミットは行うがビルド検証は Task 7 まで持ち越す。

- [ ] `git add Program.cs && git commit -m "refactor: introduce Pane class and FocusedPane helper (build intentionally broken until Task 7)"`

---

### Task 2: ナビゲーション系メソッドを Pane 引数化

**Files:**
- Modify: `Program.cs`

対象: `SelectedItemIsFileOrDrive`, `GetPreviewTarget`, `MoveUpAsync`, `MoveDownAsync`, `CancelRightSideReads`, `ValidateAndRebuildRightPaneIfNeeded`, `MoveLeft`, `MoveRightAsync`, `UpdateHorizontalScroll`, `Parent`, `RefreshCurrent`, `RefreshCurrentAsync`, `GetCurrentFullPath`, `RebuildRightSideAsync`, `GetSelectedDirectory`, `HandleResizeIfNeeded`

代表例として `MoveUpAsync` の変更前後を完全に示す。

変更前:
```csharp
    static async Task MoveUpAsync()
    {
        Column c = Columns[State.ActiveColumn];
        int visibleHeight = Console.WindowHeight - 2;

        if (c.Selected > 0)
        {
            c.Selected--;

            // Auto-scroll
            if (c.Selected < c.ScrollOffset)
                c.ScrollOffset = c.Selected;

            UpdateHorizontalScroll();

            // Reset debounce timer on every cursor move, rebuild if cursor stable for 300ms
            if (IsNavigationDebounced())
            {
                await RebuildRightSideAsync(State.ActiveColumn);
            }
            else
            {
                CancelRightSideReads(State.ActiveColumn);
                // Validate right pane matches current cursor; rebuild immediately if stale
                await ValidateAndRebuildRightPaneIfNeeded(State.ActiveColumn);
            }
            if (State.Preview.IsVisible) StartPreviewLoad();
        }
    }
```

変更後:
```csharp
    static async Task MoveUpAsync(Pane pane)
    {
        Column c = pane.Columns[pane.ActiveColumn];
        int visibleHeight = Console.WindowHeight - 2;

        if (c.Selected > 0)
        {
            c.Selected--;

            // Auto-scroll
            if (c.Selected < c.ScrollOffset)
                c.ScrollOffset = c.Selected;

            UpdateHorizontalScroll(pane);

            // Reset debounce timer on every cursor move, rebuild if cursor stable for 300ms
            if (IsNavigationDebounced())
            {
                await RebuildRightSideAsync(pane, pane.ActiveColumn);
            }
            else
            {
                CancelRightSideReads(pane, pane.ActiveColumn);
                // Validate right pane matches current cursor; rebuild immediately if stale
                await ValidateAndRebuildRightPaneIfNeeded(pane, pane.ActiveColumn);
            }
            if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        }
    }
```

残りのメソッドは同じ変換規則(先頭に `Pane pane` を追加し、本体中の `Columns`→`pane.Columns`、`State.ActiveColumn`→`pane.ActiveColumn`、`State.HorizontalScroll`→`pane.HorizontalScroll`、`State.Search`→`pane.Search`、`State.Leap`→`pane.Leap`、`State.Preview`→`pane.Preview` に置換し、既存の呼び出し引数(`columnIndex`, `restoreCursor` 等)は `pane` の直後に維持する)を適用する。

- [ ] `SelectedItemIsFileOrDrive()` → `SelectedItemIsFileOrDrive(Pane pane)` に変更。本体の `State.ActiveColumn` → `pane.ActiveColumn`、`Columns` → `pane.Columns` に置換。
- [ ] `GetPreviewTarget()` → `GetPreviewTarget(Pane pane)` に変更。内部で呼ぶ `SelectedItemIsFileOrDrive()` は `SelectedItemIsFileOrDrive(pane)` に、`Columns`/`State.ActiveColumn` は `pane.Columns`/`pane.ActiveColumn` に置換。
- [ ] `MoveUpAsync()` → `MoveUpAsync(Pane pane)`(上記の完全コード参照)。
- [ ] `MoveDownAsync()` → `MoveDownAsync(Pane pane)`。`MoveUpAsync` と対称な構造で同じ変換規則を適用。
- [ ] `CancelRightSideReads(int columnIndex)` → `CancelRightSideReads(Pane pane, int columnIndex)`。本体の `Columns` を `pane.Columns` に置換。
- [ ] `ValidateAndRebuildRightPaneIfNeeded(int columnIndex)` → `ValidateAndRebuildRightPaneIfNeeded(Pane pane, int columnIndex)`。本体の `Columns`、および内部で呼ぶ `GetSelectedDirectory(columnIndex)` → `GetSelectedDirectory(pane, columnIndex)`、`RebuildRightSideAsync(columnIndex)` → `RebuildRightSideAsync(pane, columnIndex)` に置換。
- [ ] `MoveLeft()` → `MoveLeft(Pane pane)`。`State.ActiveColumn` → `pane.ActiveColumn`、`UpdateHorizontalScroll()` → `UpdateHorizontalScroll(pane)`、`State.Preview` → `pane.Preview`、`StartPreviewLoad()` → `StartPreviewLoad(pane)` に置換。
- [ ] `MoveRightAsync()` → `MoveRightAsync(Pane pane)`。同様の置換 + `RebuildRightSideAsync(State.ActiveColumn)` → `RebuildRightSideAsync(pane, pane.ActiveColumn)`。
- [ ] `UpdateHorizontalScroll()` → `UpdateHorizontalScroll(Pane pane)`。本体の `State.Preview`/`SelectedItemIsFileOrDrive()`/`Columns`/`State.ActiveColumn`/`State.HorizontalScroll` をすべて `pane.` 系に置換(`SelectedItemIsFileOrDrive()` は `SelectedItemIsFileOrDrive(pane)`)。
- [ ] `Parent()` → `Parent(Pane pane)`。
- [ ] `RefreshCurrent()` → `RefreshCurrent(Pane pane)`。内部で `_ = RefreshCurrentAsync();` としている箇所は `_ = RefreshCurrentAsync(pane);` に変更。
- [ ] `RefreshCurrentAsync()` → `RefreshCurrentAsync(Pane pane)`。`Columns[State.ActiveColumn] = refreshed;` → `pane.Columns[pane.ActiveColumn] = refreshed;`、`RebuildRightSideAsync(State.ActiveColumn)` → `RebuildRightSideAsync(pane, pane.ActiveColumn)`。
- [ ] `GetCurrentFullPath()` → `GetCurrentFullPath(Pane pane)`。本体すべての `Columns`/`State.ActiveColumn` を `pane.` 系に置換。
- [ ] `RebuildRightSideAsync(int columnIndex)` → `RebuildRightSideAsync(Pane pane, int columnIndex)`。本体の `Columns` を `pane.Columns` に、内部で呼ぶ `GetSelectedDirectory(columnIndex)` → `GetSelectedDirectory(pane, columnIndex)`、`UpdateHorizontalScroll()` → `UpdateHorizontalScroll(pane)` に置換。`SaveCursorMemory(col)` の呼び出しはそのまま変更不要(`SaveCursorMemory`/`ApplyCursorMemory`/`SeekRestoreTo` は `Column` 引数のみを取り `Pane`/`State` を一切参照しないヘルパであり、対象メソッド完全一覧に含まれていないことを確認済み)。
- [ ] `GetSelectedDirectory(int columnIndex)` → `GetSelectedDirectory(Pane pane, int columnIndex)`。本体の `Columns` を `pane.Columns` に置換。
- [ ] `HandleResizeIfNeeded()` → `HandleResizeIfNeeded(Pane pane)`。本体の `UpdateHorizontalScroll()` → `UpdateHorizontalScroll(pane)`、ループ内の `Columns` → `pane.Columns`、`State.Preview` → `pane.Preview`、`StartPreviewLoad(forceReload: true)` → `StartPreviewLoad(pane, forceReload: true)` に置換。ただし `State.PrevWidth`/`State.PrevHeight`/`State.PrevFrame` は `ScreenState` に残る共有フレーム状態であるため `pane.` に置換せず `State.` のまま残す(この関数だけは `Pane` 固有の状態と `ScreenState` 固有の状態の両方を扱う例外であることに注意)。

- [ ] `git add Program.cs && git commit -m "refactor: add Pane parameter to navigation methods (build still broken)"`

---

### Task 3: Enter系メソッドを Pane 引数化

**Files:**
- Modify: `Program.cs`

対象: `EnterAsync`, `OpenFileAsync`, `ShowContextMenuAsync`

代表例として `EnterAsync` の変更前後を完全に示す。

変更前:
```csharp
    static async Task EnterAsync()
    {
        Column c = Columns[State.ActiveColumn];

        if (c.Entries.Count == 0)
            return;

        string name = c.Entries[c.Selected];

        if (!name.EndsWith("/"))
            return;

        if (State.ActiveColumn + 1 < Columns.Count)
        {
            State.ActiveColumn++;
        }
        else
        {
            State.ActiveColumn++;
        }

        UpdateHorizontalScroll();
        CancelPreviewLoad();
        if (State.Preview.IsVisible) StartPreviewLoad();
        await RebuildRightSideAsync(State.ActiveColumn - 1);
    }
```

変更後:
```csharp
    static async Task EnterAsync(Pane pane)
    {
        Column c = pane.Columns[pane.ActiveColumn];

        if (c.Entries.Count == 0)
            return;

        string name = c.Entries[c.Selected];

        if (!name.EndsWith("/"))
            return;

        if (pane.ActiveColumn + 1 < pane.Columns.Count)
        {
            pane.ActiveColumn++;
        }
        else
        {
            pane.ActiveColumn++;
        }

        UpdateHorizontalScroll(pane);
        CancelPreviewLoad(pane);
        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
        await RebuildRightSideAsync(pane, pane.ActiveColumn - 1);
    }
```

- [ ] `EnterAsync()` → `EnterAsync(Pane pane)`(上記の完全コード参照)。
- [ ] `OpenFileAsync()` → `OpenFileAsync(Pane pane)`。本体の `Columns`/`State.ActiveColumn` を `pane.` 系に置換し、`GetCurrentFullPath()` → `GetCurrentFullPath(pane)` に置換。`LaunchFileAsync(filePath)` は `Pane` を参照しないため引数変更不要。
- [ ] `ShowContextMenuAsync()` → `ShowContextMenuAsync(Pane pane)`。同様に `Columns`/`State.ActiveColumn` を `pane.` 系に置換し、`GetCurrentFullPath()` → `GetCurrentFullPath(pane)` に置換。`LaunchContextMenuAsync(filePath)` は変更不要。

- [ ] `git add Program.cs && git commit -m "refactor: add Pane parameter to enter/open methods (build still broken)"`

---

### Task 4: プレビュー系メソッドを Pane 引数化

**Files:**
- Modify: `Program.cs`

対象: `StartPreviewLoad`, `CancelPreviewLoad`

代表例として `CancelPreviewLoad` の変更前後を完全に示す(短いため全体を掲載)。

変更前:
```csharp
    static void CancelPreviewLoad()
    {
        State.Preview.Cancel();
        State.Preview.Content = null;
        State.Preview.CurrentPath = null;
    }
```

変更後:
```csharp
    static void CancelPreviewLoad(Pane pane)
    {
        pane.Preview.Cancel();
        pane.Preview.Content = null;
        pane.Preview.CurrentPath = null;
    }
```

- [ ] `CancelPreviewLoad()` → `CancelPreviewLoad(Pane pane)`(上記の完全コード参照)。
- [ ] `StartPreviewLoad(bool forceReload = false)` → `StartPreviewLoad(Pane pane, bool forceReload = false)`。本体すべての `State.Preview`/`State.ActiveColumn`/`Columns`/`State.HorizontalScroll` を `pane.` 系に置換。内部で呼ぶ `GetPreviewTarget()` → `GetPreviewTarget(pane)` に置換。**注意**: `for (int i = State.HorizontalScroll; i < Columns.Count && ...)` のループは `pane.HorizontalScroll`/`pane.Columns.Count` に置換する。Task.Run 内のラムダで `State.Preview.Content = content;` としている箇所も `pane.Preview.Content = content;` に置換する(クロージャが `pane` をキャプチャする形になる。これはスレッドセーフ上の懸念に見えるが、既存コードも同じく `Task.Run` 内で `State.Preview` という共有参照を直接書き換えており、Phase 1 ではこの並行性の性質を変えない — 単一ペイン運用の間は `pane` は常に同じインスタンスであり、既存の挙動と等価)。

- [ ] `git add Program.cs && git commit -m "refactor: add Pane parameter to preview methods (build still broken)"`

---

### Task 5: 検索系メソッドを Pane 引数化

**Files:**
- Modify: `Program.cs`

対象: `RecomputeMatchesAsync`, `EnterSearchMode`, `ExitSearchMode`, `HandleSearchKeyAsync`

代表例として `RecomputeMatchesAsync`(`lock (_searchLock)` を含む例外パターンの実例)の変更前後を完全に示す。

変更前:
```csharp
    static async Task RecomputeMatchesAsync()
    {
        // Cancel any in-progress scan
        State.Search.SearchCts?.Cancel();
        State.Search.SearchCts?.Dispose();
        var cts = new CancellationTokenSource();
        State.Search.SearchCts = cts;

        string query = State.Search.Query;
        bool regexMode = State.Search.RegexMode;
        int anchor = State.Search.Anchor;
        Column col = Columns[State.ActiveColumn];

        lock (_searchLock)
        {
            State.Search.Matches.Clear();
            State.Search.SearchDone = false;
            State.Search.NeedsRecompute = false;
        }

        await Task.Run(() =>
        {
            for (int i = 0; i < col.Entries.Count; i++)
            {
                if (cts.Token.IsCancellationRequested) return;
                string name = col.Entries[i].TrimEnd('/');
                if (SearchHelper.MatchesSearchQuery(name, query, regexMode, _migemo))
                {
                    lock (_searchLock) { State.Search.Matches.Add(i); }
                }
            }

            if (cts.Token.IsCancellationRequested) return;

            lock (_searchLock)
            {
                State.Search.MatchIndex = SearchHelper.FindNearestMatchIndex(State.Search.Matches, anchor);
                if (State.Search.Matches.Count > 0)
                    col.Selected = State.Search.Matches[State.Search.MatchIndex];
                State.Search.SearchDone = true;
            }
        }, cts.Token);
    }
```

変更後:
```csharp
    static async Task RecomputeMatchesAsync(Pane pane)
    {
        // Cancel any in-progress scan
        pane.Search.SearchCts?.Cancel();
        pane.Search.SearchCts?.Dispose();
        var cts = new CancellationTokenSource();
        pane.Search.SearchCts = cts;

        string query = pane.Search.Query;
        bool regexMode = pane.Search.RegexMode;
        int anchor = pane.Search.Anchor;
        Column col = pane.Columns[pane.ActiveColumn];

        lock (_searchLock)
        {
            pane.Search.Matches.Clear();
            pane.Search.SearchDone = false;
            pane.Search.NeedsRecompute = false;
        }

        await Task.Run(() =>
        {
            for (int i = 0; i < col.Entries.Count; i++)
            {
                if (cts.Token.IsCancellationRequested) return;
                string name = col.Entries[i].TrimEnd('/');
                if (SearchHelper.MatchesSearchQuery(name, query, regexMode, _migemo))
                {
                    lock (_searchLock) { pane.Search.Matches.Add(i); }
                }
            }

            if (cts.Token.IsCancellationRequested) return;

            lock (_searchLock)
            {
                pane.Search.MatchIndex = SearchHelper.FindNearestMatchIndex(pane.Search.Matches, anchor);
                if (pane.Search.Matches.Count > 0)
                    col.Selected = pane.Search.Matches[pane.Search.MatchIndex];
                pane.Search.SearchDone = true;
            }
        }, cts.Token);
    }
```

`_searchLock` 自体は共有 static のまま変更しない(変換規則9)。ロックが保護する対象が `State.Search` から `pane.Search` に変わるだけである。

- [ ] `RecomputeMatchesAsync()` → `RecomputeMatchesAsync(Pane pane)`(上記の完全コード参照)。
- [ ] `EnterSearchMode()` → `EnterSearchMode(Pane pane)`。本体の `Columns`/`State.ActiveColumn`/`State.Search` を `pane.` 系に置換。`lock (_searchLock) { State.Search.Matches... }` は `pane.Search.Matches` に置換(ロックは維持)。
- [ ] `ExitSearchMode(bool restoreCursor)` → `ExitSearchMode(Pane pane, bool restoreCursor)`。本体の `State.Search`/`Columns`/`State.ActiveColumn` を `pane.` 系に置換。`lock (_searchLock)` 内側も同様。
- [ ] `HandleSearchKeyAsync(ConsoleKeyInfo key)` → `HandleSearchKeyAsync(Pane pane, ConsoleKeyInfo key)`。本体の `State.Search`/`Columns`/`State.ActiveColumn`/`State.Preview` を `pane.` 系に置換。内部で呼ぶ `ExitSearchMode(restoreCursor: true)` → `ExitSearchMode(pane, restoreCursor: true)`、`UpdateHorizontalScroll()` → `UpdateHorizontalScroll(pane)`、`RebuildRightSideAsync(State.ActiveColumn)` → `RebuildRightSideAsync(pane, pane.ActiveColumn)`、`StartPreviewLoad()` → `StartPreviewLoad(pane)` に置換。`lock (_searchLock) { if (s.Matches.Count > 0) ... }` の `s` はローカル変数 `SearchState s = pane.Search;` に変わるだけで、ロック内側のコードは変更不要(`s` 経由の参照のまま)。

- [ ] `git add Program.cs && git commit -m "refactor: add Pane parameter to search methods (build still broken)"`

---

### Task 6: Leap系メソッドを Pane 引数化

**Files:**
- Modify: `Program.cs`

対象: `EnterLeapMode`, `ExitLeapModeAsync`, `HandleLeapKeyAsync`, `EnterDirInLeapAsync`, `TryLeapAutoEnterAsync`, `LeapStepUpAsync`, `GoLeapRootAsync`, `MoveLeapCursorDown`, `MoveLeapCursorUp`, `ApplyLeapFilter`

代表例として `EnterLeapMode` の変更前後を完全に示す。

変更前:
```csharp
    static void EnterLeapMode()
    {
        Column col = Columns[State.ActiveColumn];
        State.Leap.Active = true;
        State.Leap.Buffer = "";
        State.Leap.RootDir = col.Path;
        State.Leap.RootColumnIndex = State.ActiveColumn;
        State.Leap.RootCursorIndex = col.Selected;
        State.Leap.DirStack.Clear();
        State.Leap.NeedsRecompute = false;
        State.Leap.NoMatch = false;
        State.Leap.VisibleIndices.Clear();
        State.Leap.ColumnScrollOffset = 0;
        State.Leap.PreviewWasVisible = State.Preview.IsVisible;
        if (State.Preview.IsVisible)
        {
            CancelPreviewLoad();
            State.Preview.IsVisible = false;
        }
        UpdateHorizontalScroll();
        ApplyLeapFilter(); // empty filter = all entries visible
    }
```

変更後:
```csharp
    static void EnterLeapMode(Pane pane)
    {
        Column col = pane.Columns[pane.ActiveColumn];
        pane.Leap.Active = true;
        pane.Leap.Buffer = "";
        pane.Leap.RootDir = col.Path;
        pane.Leap.RootColumnIndex = pane.ActiveColumn;
        pane.Leap.RootCursorIndex = col.Selected;
        pane.Leap.DirStack.Clear();
        pane.Leap.NeedsRecompute = false;
        pane.Leap.NoMatch = false;
        pane.Leap.VisibleIndices.Clear();
        pane.Leap.ColumnScrollOffset = 0;
        pane.Leap.PreviewWasVisible = pane.Preview.IsVisible;
        if (pane.Preview.IsVisible)
        {
            CancelPreviewLoad(pane);
            pane.Preview.IsVisible = false;
        }
        UpdateHorizontalScroll(pane);
        ApplyLeapFilter(pane); // empty filter = all entries visible
    }
```

- [ ] `EnterLeapMode()` → `EnterLeapMode(Pane pane)`(上記の完全コード参照)。
- [ ] `ExitLeapModeAsync(bool restoreCursor)` → `ExitLeapModeAsync(Pane pane, bool restoreCursor)`。本体の `State.Leap`/`State.ActiveColumn`/`Columns`/`State.Preview` を `pane.` 系に置換。`UpdateHorizontalScroll()` → `UpdateHorizontalScroll(pane)`、`RebuildRightSideAsync(State.ActiveColumn)` → `RebuildRightSideAsync(pane, pane.ActiveColumn)`、`StartPreviewLoad()` → `StartPreviewLoad(pane)` に置換。
- [ ] `HandleLeapKeyAsync(ConsoleKeyInfo key)` → `HandleLeapKeyAsync(Pane pane, ConsoleKeyInfo key)`。本体冒頭の `LeapState leap = State.Leap; Column col = Columns[State.ActiveColumn];` は `LeapState leap = pane.Leap; Column col = pane.Columns[pane.ActiveColumn];` に置換。以降 `leap`/`col` 経由の参照は変更不要。内部で呼ぶ全メソッド(`ExitLeapModeAsync`, `EnterDirInLeapAsync`, `LeapStepUpAsync`, `MoveLeapCursorDown`, `MoveLeapCursorUp`, `GoLeapRootAsync`, `GetCurrentFullPath`, `LaunchFileAsync`)の呼び出しに `pane` を先頭引数として渡す(`LaunchFileAsync` は `Pane` を取らないため `pane` を渡さない)。
- [ ] `EnterDirInLeapAsync()` → `EnterDirInLeapAsync(Pane pane)`。本体の `Columns`/`State.ActiveColumn`/`State.Leap` を `pane.` 系に置換。`UpdateHorizontalScroll()` → `UpdateHorizontalScroll(pane)`、`RebuildRightSideAsync(State.ActiveColumn - 1)` → `RebuildRightSideAsync(pane, pane.ActiveColumn - 1)`、`ApplyLeapFilter()` → `ApplyLeapFilter(pane)`、`TryLeapAutoEnterAsync()` → `TryLeapAutoEnterAsync(pane)`。
- [ ] `TryLeapAutoEnterAsync()` → `TryLeapAutoEnterAsync(Pane pane)`。本体の `State.Leap`/`Columns`/`State.ActiveColumn` を `pane.` 系に置換。`EnterDirInLeapAsync()` → `EnterDirInLeapAsync(pane)`。
- [ ] `LeapStepUpAsync()` → `LeapStepUpAsync(Pane pane)`。本体の `State.ActiveColumn`/`State.Leap` を `pane.` 系に置換。`UpdateHorizontalScroll()` → `UpdateHorizontalScroll(pane)`、`RebuildRightSideAsync(State.ActiveColumn)` → `RebuildRightSideAsync(pane, pane.ActiveColumn)`、`ApplyLeapFilter()` → `ApplyLeapFilter(pane)`。
- [ ] `GoLeapRootAsync()` → `GoLeapRootAsync(Pane pane)`。同様の置換。
- [ ] `MoveLeapCursorDown()` → `MoveLeapCursorDown(Pane pane)`。本体の `State.Leap`/`Columns`/`State.ActiveColumn` を `pane.` 系に置換。
- [ ] `MoveLeapCursorUp()` → `MoveLeapCursorUp(Pane pane)`。同様の置換。
- [ ] `ApplyLeapFilter()` → `ApplyLeapFilter(Pane pane)`。本体冒頭の `LeapState leap = State.Leap; Column col = Columns[State.ActiveColumn];` を `LeapState leap = pane.Leap; Column col = pane.Columns[pane.ActiveColumn];` に置換。以降 `leap`/`col` 経由の参照は変更不要。

- [ ] `git add Program.cs && git commit -m "refactor: add Pane parameter to leap methods (build still broken)"`

---

### Task 7: HandleSpecialKeyAsync + Main のメインループを Pane 引数化し、ビルド・テストを通す

**Files:**
- Modify: `Program.cs`

この Task で残る唯一の呼び出し起点である `Main()` を `FocusedPane` 経由に切り替え、`HandleSpecialKeyAsync` を `Pane pane` 引数化することで、Task 1〜6 で個別に変換した全メソッドへの呼び出し連鎖が完成し、ビルドが初めて通る。

`HandleSpecialKeyAsync` は変更前、引数なし(`ConsoleKeyInfo key` のみ)で `switch (key.Key)` の各 `case` 内から `Columns`/`State.ActiveColumn`/`State.Preview`/`NavigationHelper.PageUp(Columns[State.ActiveColumn], ...)` 等を直接参照し、`MoveUpAsync()`/`ShowContextMenuAsync()`/`OpenFileAsync()`/`EnterAsync()`/`Parent()`/`RefreshCurrent()`/`EnterLeapMode()`/`EnterSearchMode()` を無引数で呼んでいる(全文は本ファイルの Task 2/3/6 に列挙した各メソッドの「変更前」と同型のパターンの寄せ集めであり、新規パターンはない)。変更後の全文を示す。
```csharp
    static async Task HandleSpecialKeyAsync(Pane pane, ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                await MoveUpAsync(pane);
                break;

            case ConsoleKey.DownArrow:
                await MoveDownAsync(pane);
                break;

            case ConsoleKey.LeftArrow:
                MoveLeft(pane);
                break;

            case ConsoleKey.RightArrow:
                await MoveRightAsync(pane);
                break;

            case ConsoleKey.Enter:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    await ShowContextMenuAsync(pane);
                else if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    await OpenFileAsync(pane);
                else
                    await EnterAsync(pane);
                break;

            case ConsoleKey.Backspace:
                Parent(pane);
                break;

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

            case ConsoleKey.L:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    RefreshCurrent(pane);
                break;

            case ConsoleKey.F3:
                EnterLeapMode(pane);
                break;

            case ConsoleKey.F5:
                RefreshCurrent(pane);
                break;

            case ConsoleKey.PageUp:
                NavigationHelper.PageUp(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                UpdateHorizontalScroll(pane);
                await RebuildRightSideAsync(pane, pane.ActiveColumn);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;

            case ConsoleKey.PageDown:
                NavigationHelper.PageDown(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                UpdateHorizontalScroll(pane);
                await RebuildRightSideAsync(pane, pane.ActiveColumn);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;

            case ConsoleKey.Home:
                NavigationHelper.GoHome(pane.Columns[pane.ActiveColumn]);
                UpdateHorizontalScroll(pane);
                await RebuildRightSideAsync(pane, pane.ActiveColumn);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;

            case ConsoleKey.End:
                NavigationHelper.GoEnd(pane.Columns[pane.ActiveColumn]);
                UpdateHorizontalScroll(pane);
                await RebuildRightSideAsync(pane, pane.ActiveColumn);
                if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                break;

            case ConsoleKey.G:
                if (key.Modifiers == ConsoleModifiers.None)
                {
                    // g → top (must have NO modifiers)
                    NavigationHelper.GoHome(pane.Columns[pane.ActiveColumn]);
                    UpdateHorizontalScroll(pane);
                    await RebuildRightSideAsync(pane, pane.ActiveColumn);
                    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                }
                else if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                {
                    // G → bottom
                    NavigationHelper.GoEnd(pane.Columns[pane.ActiveColumn]);
                    UpdateHorizontalScroll(pane);
                    await RebuildRightSideAsync(pane, pane.ActiveColumn);
                    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                }
                break;

            case ConsoleKey.B:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    NavigationHelper.PageUp(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                    UpdateHorizontalScroll(pane);
                    await RebuildRightSideAsync(pane, pane.ActiveColumn);
                    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                }
                break;

            case ConsoleKey.F:
                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    NavigationHelper.PageDown(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                    UpdateHorizontalScroll(pane);
                    await RebuildRightSideAsync(pane, pane.ActiveColumn);
                    if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                }
                break;

            default:
                if (key.KeyChar == '/' && key.Modifiers == ConsoleModifiers.None)
                    EnterSearchMode(pane);
                else if (key.KeyChar == '\\' && key.Modifiers == ConsoleModifiers.None)
                    await ShowContextMenuAsync(pane);
                break;
        }
    }
```

- [ ] `HandleSpecialKeyAsync(ConsoleKeyInfo key)` を上記の完全コードに置き換える。

`Main()` のメインループ部分を変更する(`static async Task Main()` 全体のうち、`Columns`/`State.` を直接参照している箇所のみが変わる。ファイル冒頭の `Console.OutputEncoding` 等の初期化行は変更不要)。変更前の状態は Task 1 で `State.Panes.Add(new Pane());` 等を追加した直後の姿(`RebuildRightSideAsync(0)` はまだ旧シグネチャの呼び出しのまま、ループ内は無引数の `State.Leap.Active` 等の直接参照、`HandleLeapKeyAsync(key)`/`HandleSearchKeyAsync(key)`/`HandleSpecialKeyAsync(key)` も無引数呼び出しのまま)である。変更後の全文を示す。
```csharp
        try
        {
            string root = GetRootPath();
            State.Panes.Add(new Pane());
            State.FocusedPane = 0;
            FocusedPane.Columns.Add(CreateColumn(root));
            await RebuildRightSideAsync(FocusedPane, 0);

            while (true)
            {
                HandleResizeIfNeeded(FocusedPane);
                Draw();

                if (!Console.KeyAvailable)
                {
                    Pane pane = FocusedPane;
                    if (pane.Leap.Active
                        && pane.Leap.NeedsRecompute
                        && (DateTime.UtcNow - pane.Leap.LastInputTime).TotalMilliseconds >= NavigationDebounceMs)
                    {
                        pane.Leap.NeedsRecompute = false;
                        ApplyLeapFilter(pane);
                        await TryLeapAutoEnterAsync(pane); // auto-enter single directory match
                    }
                    else if (pane.Search.Active
                        && pane.Search.NeedsRecompute
                        && (DateTime.UtcNow - pane.Search.LastInputTime).TotalMilliseconds >= 300)
                    {
                        await RecomputeMatchesAsync(pane);
                        UpdateHorizontalScroll(pane);
                        await RebuildRightSideAsync(pane, pane.ActiveColumn);
                        if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                    }
                    System.Threading.Thread.Sleep(50);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(true);
                Pane focusedPane = FocusedPane;

                if (focusedPane.Leap.Active)
                {
                    await HandleLeapKeyAsync(focusedPane, key);
                    continue;
                }

                if (focusedPane.Search.Active)
                {
                    await HandleSearchKeyAsync(focusedPane, key);
                    continue;
                }

                switch (key.KeyChar)
                {
                    case (char)27: // Esc
                        return;

                    default:
                        await HandleSpecialKeyAsync(focusedPane, key);
                        break;
                }
            }
        }
```

- [ ] `Main()` 内の該当ブロックを上記の完全コードに置き換える。

`Draw()` / `BuildFrame` / `DrawColumnToLines` は、変換規則11に従い `Columns`/`State.ActiveColumn`/`State.HorizontalScroll`/`State.Search`/`State.Leap`/`State.Preview` への参照を `FocusedPane.Columns` 等に読み替える(シグネチャは変えない)。`BuildFrame` 内の代表的な変更箇所を示す。

変更前(`BuildFrame` 冒頭〜カラム描画ループ):
```csharp
        bool showPreview = State.Preview.IsVisible && Console.WindowWidth >= ColumnWidth * 2;
        bool filePreview = State.Preview.IsVisible && SelectedItemIsFileOrDrive();

        int displayX = 0;
        int drawnColumnsWidth = 0;
        int columnDrawLimit = filePreview ? width - ColumnWidth : width;

        for (int i = State.HorizontalScroll;
             i < Columns.Count && displayX + ColumnWidth <= columnDrawLimit;
             i++)
        {
            bool isFirstVisible = (i == State.HorizontalScroll);
            DrawColumnToLines(lines, Columns[i], displayX, i == State.ActiveColumn, i < State.ActiveColumn, width, height, isFirstVisible);
            displayX += ColumnWidth;
            drawnColumnsWidth += ColumnWidth;
        }
```

変更後:
```csharp
        Pane pane = FocusedPane;
        bool showPreview = pane.Preview.IsVisible && Console.WindowWidth >= ColumnWidth * 2;
        bool filePreview = pane.Preview.IsVisible && SelectedItemIsFileOrDrive(pane);

        int displayX = 0;
        int drawnColumnsWidth = 0;
        int columnDrawLimit = filePreview ? width - ColumnWidth : width;

        for (int i = pane.HorizontalScroll;
             i < pane.Columns.Count && displayX + ColumnWidth <= columnDrawLimit;
             i++)
        {
            bool isFirstVisible = (i == pane.HorizontalScroll);
            DrawColumnToLines(lines, pane.Columns[i], displayX, i == pane.ActiveColumn, i < pane.ActiveColumn, width, height, isFirstVisible);
            displayX += ColumnWidth;
            drawnColumnsWidth += ColumnWidth;
        }
```

- [ ] `BuildFrame` 冒頭のカラム描画ループを上記の完全コードに置き換える(ローカル変数 `Pane pane = FocusedPane;` を関数冒頭で1回だけ宣言し、以降 `BuildFrame` 内の全 `State.Preview`/`State.ActiveColumn`/`State.HorizontalScroll`/`Columns`/`State.Search`/`State.Leap` 参照をこの `pane` 経由に統一する)。
- [ ] `BuildFrame` 内、プレビュー描画ブロック(`if (showPreview) { ... State.Preview.IsLoading ... State.Preview.Content ... }`)の `State.Preview` を `pane.Preview` に置換。
- [ ] `BuildFrame` 内、`string fullPath = GetCurrentFullPath();` を `string fullPath = GetCurrentFullPath(pane);` に置換。
- [ ] `BuildFrame` 内、`if (State.Preview.IsVisible && SelectedItemIsFileOrDrive() && Console.WindowWidth < ColumnWidth * 2)` を `if (pane.Preview.IsVisible && SelectedItemIsFileOrDrive(pane) && Console.WindowWidth < ColumnWidth * 2)` に置換。
- [ ] `BuildFrame` 内のステータス行分岐:
  - `if (State.Leap.Active)` → `if (pane.Leap.Active)`。`SearchHelper.BuildLeapStatusBar(State.Leap, Columns, State.ActiveColumn, width)` → `SearchHelper.BuildLeapStatusBar(pane.Leap, pane.Columns, pane.ActiveColumn, width)`。
  - `else if (State.Search.Active)` → `else if (pane.Search.Active)`。`lock (_searchLock) { matchSnapshot = new List<int>(State.Search.Matches); done = State.Search.SearchDone; }` → `lock (_searchLock) { matchSnapshot = new List<int>(pane.Search.Matches); done = pane.Search.SearchDone; }`。`SearchHelper.BuildSearchStatusBar(State.Search, matchSnapshot, done, width)` → `SearchHelper.BuildSearchStatusBar(pane.Search, matchSnapshot, done, width)`。
  - `else { ... string previewHint = State.Preview.IsVisible ? ... }` → `pane.Preview.IsVisible` に置換。
- [ ] `DrawColumnToLines` 内、`bool inLeap = active && State.Leap.Active;` および以降の `State.Leap.VisibleIndices`/`State.Leap.ColumnScrollOffset` 参照、`bool inSearch = active && State.Search.Active;` および `State.Search.Matches`/`State.Search.MatchIndex` 参照を、すべて `FocusedPane.Leap`/`FocusedPane.Search` に置換する(この関数はシグネチャに `Pane` を取らないため、関数内で `Pane pane = FocusedPane;` をローカル変数として冒頭に追加し、以降はそれ経由で参照する)。

- [ ] `dotnet build Zurari.csproj` を実行し、exit code 0 で成功することを確認する。ここで初めてビルドが通る。
  ```
  dotnet build Zurari.csproj
  ```
  期待結果: `ビルドに成功しました。` / exit code 0。

- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、全テストが緑であることを確認する。
  ```
  dotnet test Zurari.Tests/Zurari.Tests.csproj
  ```
  期待結果: `合格: 53、失敗: 0`(前提条件で確認した件数と同じ)。

- [ ] `git add Program.cs && git commit -m "refactor: route Main loop and rendering through FocusedPane; build and tests green"`

---

### Task 8: 手動での見た目同一確認

**Files:** なし(手動確認のみ)

- [x] `dotnet run --project Zurari.csproj`(または `dotnet run` をリポジトリルートで実行)でアプリを起動する。
- [x] 起動直後の画面(カラム一覧・パス行・ステータス行)が Phase 1 着手前と同じ見た目であることを目視確認する。期待画面: 左端カラムにドライブ一覧(Windows の場合)またはルートディレクトリの一覧が表示され、最下部2行にフルパスとヒント文字列(`Esc=Quit | ...`)が表示される。
- [x] 矢印キーで上下左右にカーソルを移動し、カラムが増減しプレビューが更新されることを確認する(変更前と同じ挙動)。
- [x] `/` で検索モードに入り、文字を入力してハイライトされること、`Esc` で元のカーソル位置に戻ることを確認する。
- [x] `F3` で Leap モードに入り、文字を入力してフィルタされること、`Esc` で元のカーソル位置に戻ることを確認する。
- [x] `Shift+V` でプレビューの表示/非表示が切り替わることを確認する。
- [x] `Esc` でアプリを終了し、異常終了しないことを確認する。
- [x] 上記すべてで異常(クラッシュ・フリーズ・表示崩れ)がなければ、このタスクにはコミットすべきコード変更がないため、コミットは不要(手動確認のみのタスクである旨を実行ログに残す)。

---

## 完了条件チェックリスト
- [x] `Pane` クラスが spec の API 契約通り(`Columns`, `ActiveColumn`, `HorizontalScroll`, `Search`, `Leap`, `Preview`, `NeedsRefresh`)に定義されている。
- [x] `ScreenState` が `PrevFrame`/`PrevWidth`/`PrevHeight`/`Panes`/`FocusedPane`/`SplitActive`/`Dialog` のみを持つ形に縮小されている。
- [x] `FocusedPane` ヘルパ(`static Pane FocusedPane => State.Panes[State.FocusedPane];`)が定義されている。
- [x] 対象メソッド完全一覧に列挙した全メソッド(クラスタA〜F)が `Pane pane` を先頭引数として受け取る形にリファクタリングされている。
- [x] `Draw`/`BuildFrame`/`DrawColumnToLines`/`DrawPreviewToLines` はシグネチャを変えずに `FocusedPane` 経由で状態を参照する形になっている(Phase 2 での3層分解に備えた状態のまま)。
- [x] `_migemo`/`_lastErrorMessage`/`_cursorMemory`/`_cursorMemoryKeys`/`_lastNavigationTime`/`_searchLock` が共有 static のまま変更されていない。
- [x] `dotnet build Zurari.csproj` が exit code 0 で成功する(確認済み: エラー0、警告0)。
- [x] `dotnet test Zurari.Tests/Zurari.Tests.csproj` が全緑(合格: 64、失敗: 0、スキップ: 0)。
- [x] 手動確認(Task 8)で Phase 1 着手前と見た目・挙動が同一であることを確認済み(ユーザーによる実ターミナルでの目視確認完了)。

## 未解決事項
(実行セッションが判断保留を記録する欄。空のまま残すこと)

- 計画作成時の想定 HEAD(928735a)よりも、実装開始時点の実際の HEAD が2コミット進んでいた(`f95f4a6`: RedrawGate 描画最適化、`d863453`: Leap 子ペイン再構築修正)。この計画のコードスニペットは「代表例」であり、本体は「変換規則(1〜11)」であるという前提に基づき、実コードに変換規則を機械的に適用して対応した。具体的な差分:
  - 新規メソッド `AnyColumnLoading()` / `UiActivityInProgress()`(RedrawGate コミットで追加。`Columns`/`State.Preview`/`State.Search` を参照)をクラスタA(ナビゲーション、Task 2)に含めて `Pane` 引数化した。
  - `MoveLeapCursorDown`/`MoveLeapCursorUp` は既に `...Async` 化されていたため、そのまま `MoveLeapCursorDownAsync(Pane pane)`/`MoveLeapCursorUpAsync(Pane pane)` とした。
  - 新規 `RebuildRightSideOrValidateAsync()`(`d863453` で追加)も `Pane` 引数化した。
- Task 8(手動での見た目同一確認)について: この環境(Windows / Git Bash / 非対話プロセス起動)ではフルスクリーン TUI アプリを自動化経由で起動すると `System.IO.IOException: ハンドルが無効です`(`ConsolePal.GetBufferInfo` / `get_WindowHeight` 由来、`Program.cs` の `CreateColumnAsync` → `RebuildRightSideAsync` 経由)がスローされ、自動確認ができない。
  - **切り分け実施済み**: リファクタ前コミット(`928735a`)の `Program.cs` を一時的に `git checkout 928735a -- Program.cs` で復元し、同じ非対話起動テスト(`ProcessStartInfo` + `RedirectStandardError`、`UseShellExecute=$false`)を実行したところ、全く同一のスタックトレース(`CreateColumnAsync` → `RebuildRightSideAsync` → `Main`)で同じ `System.IO.IOException` が発生した。
  - **結論**: これは Pane 抽出リファクタによる回帰ではなく、「実コンソールが無い非対話起動では常に発生する」既存の制約である。テスト後、`git checkout HEAD -- Program.cs` で復元し、`dotnet build Zurari.csproj` が成功することを確認済み(0エラー)。
  - Task 8 の手動確認(矢印キー操作、`/` 検索、`F3` Leap、`Shift+V` プレビュー切替、`Esc` 終了)は、実際のターミナル(Windows Terminal / cmd.exe / 通常の PowerShell ウィンドウ)上でユーザー自身が実行する必要がある。下記チェックリストを参照。
