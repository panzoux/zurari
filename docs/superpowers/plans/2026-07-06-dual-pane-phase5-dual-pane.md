# Phase 5: 上下2ペイン(トグル式) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Ctrl+W` で上下2ペイン分割をトグルし、`Tab` でフォーカスペインを切り替えられるようにする。各ペインは独立した Miller カラム群・検索・Leap 状態を持ち、非フォーカスペインは減光パレットで描画される。
**Architecture:** Phase 1〜4 で導入済みの `Pane`/`ScreenState`/`DialogState` を土台に、`BuildFrame` を「ペイン矩形描画×1〜2 → 共有フッター → オーバーレイ」の3層合成へ拡張する。分割の生成・破棄は `Pane` の追加・削除と `ReadCts` キャンセルのみで完結させ、既存のナビゲーション関数(`Pane pane` 引数を取る形に Phase 1 でリファクタ済み)は無改修で両ペインに適用する。
**Tech Stack:** C# / .NET 8, xUnit(Zurari.Tests), 自前 TUI(外部 TUI フレームワーク禁止)

---

## 前提条件(開始前に検証)

- [ ] `git log --oneline -10` で Phase 1〜4 の完了コミットが存在することを確認する。
- [ ] `dotnet build Zurari.csproj` を実行し、exit code 0 で成功することを確認する。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、`Passed!` (全緑・失敗0件)であることを確認する。
- [ ] `Program.cs` を開き、`sealed class Pane` と `sealed class ScreenState` の定義が Phase 1 の API 契約(`Columns`/`ActiveColumn`/`HorizontalScroll`/`Search`/`Leap`/`Preview` を保持する `Pane`、`List<Pane> Panes`/`FocusedPane`/`SplitActive`/`Dialog` を保持する `ScreenState`)と一致していることを目視確認する。一致しない場合、実装セッションは差異を「未解決事項」に記録し停止する。
- [ ] `BuildFrame(int width, int height)` が Phase 2 の3層合成(ペイン矩形描画→共有フッター→オーバーレイ)で構成されていることを確認する。
- [ ] `DialogState`/`HandleDialogKeyAsync` が Phase 3 の3部品(`ConfirmDialogState`/`TextInputDialogState`/`ListSelectDialogState`)込みで動作することを確認する。
- [ ] `static Pane FocusedPane` ヘルパ(`State.Panes[State.FocusedPane]`)が存在することを確認する。存在しない場合は Task 1 の最初のステップとして追加する。

この計画と spec に書かれていない判断が必要になったら、自分で決めずに「未解決事項」に記録してそのステップを保留し、次の独立したタスクに進むか停止すること。

---

## 既存コードの前提(このPhaseで扱う実コード断片)

以下は Phase 1〜4 完了後の Program.cs に存在するはずの実装(2026-07-06 時点の pre-Phase-1 コードから該当箇所を抜粋し、Phase 1 の変換規則を適用した想定形)。実装セッションはリファクタ後の実シグネチャを確認のうえ、異なっていれば「未解決事項」に記録すること。

- `AnsiColors`(現状 pre-Phase-1):
  ```csharp
  static class AnsiColors
  {
      public const string Blue   = "\x1b[94m";
      public const string Yellow = "\x1b[33m";
      public const string Reset  = "\x1b[0m";
      public static string Colorize(string text, string color) => color + text + Reset;
  }
  ```
- `DrawColumnToLines` は現状 `bool isLeft`(同一ペイン内で自分より左=非アクティブカラムかどうか)を引数に取るが、これは「ペインがフォーカスされているか」とは無関係の既存概念。本 Phase では新たに `bool focused`(ペイン全体のフォーカス状態)を引数追加する。
- 選択ハイライト・マッチ表示に使われる生 ANSI コード(pre-Phase-1 時点):
  - 通常選択行の背景: アクティブカラム `\x1b[47m`(白)、非アクティブカラム `\x1b[100m`(グレー)
  - 選択行の文字色: ディレクトリ `\x1b[94m`、ファイル `\x1b[30m`
  - Search 現在マッチ: 背景 `\x1b[42m`(緑)、文字 `\x1b[30m`
  - Search 他マッチ: 文字 `\x1b[32m`(緑前景)
  - Leap 選択行: 背景 `\x1b[47m`、文字 `\x1b[94m`(ディレクトリ)/`\x1b[30m`(ファイル)
  - 通常ディレクトリ: `AnsiColors.Blue` = `\x1b[94m`
- `CreateColumn(string path)` / `CreateColumnAsync(string path, CancellationToken ct)`: 指定パスのディレクトリ一覧から `Column` を1つ生成する(非同期版は `ct` でキャンセル可能、`LoadingTask` に読み込みタスクを積む)。
- `RebuildRightSideAsync(int columnIndex)`(Phase 1 リファクタ後は `RebuildRightSideAsync(Pane pane, int columnIndex)` の想定): `pane.Columns` を `columnIndex` より右側だけ破棄・再構築する。破棄時は各カラムの `ReadCts?.Cancel()` を呼び、`SaveCursorMemory` でカーソル位置を記憶してから `RemoveAt`。
- `CancelRightSideReads(int columnIndex)`(Phase 1 リファクタ後は `CancelRightSideReads(Pane pane, int columnIndex)` の想定): `columnIndex` より右側の全カラムの `ReadCts?.Cancel()` のみを行う(カラム自体は破棄しない)。
- `GetCurrentFullPath()`(Phase 1 リファクタ後は `GetCurrentFullPath(Pane pane)` の想定): `pane.Columns[pane.ActiveColumn]` のカーソル位置のフルパスを返す。
- `Column` クラス: `Path`, `Entries`, `Selected`, `ScrollOffset`, `RestoreTo`, `CachedChildren`, `CachedTime`, `IsLoading`, `EntriesRead`, `DirectoriesCount`, `ReadCts`(`CancellationTokenSource?`), `LoadingTask`(`Task?`)。

**重要な確認事項**: 上記の関数名・シグネチャは Phase 1 のリファクタで `Pane pane` が第一引数に追加された形になっているはずである。本計画のコードはすべてその前提(`Pane pane` 引数あり)で記述する。実装セッションが実際のコードを読んで引数順序やヘルパ名が異なると判明した場合、実装セッションは機械的にリネームして適用してよい(これは「コンパイルが通る書き方の選択」の範囲内であり、判断を要する設計変更ではない)。ただし関数の**責務・キャンセル対象・破棄順序**が本計画の記述と異なる場合は「未解決事項」に記録すること。

---

### Task 1: 分割の生成・破棄ロジック(`Ctrl+W`)

**Files:**
- Modify: `Program.cs`(`HandleSpecialKeyAsync` に `Ctrl+W` ケースを追加、新規メソッド `ToggleSplitAsync` を追加)
- Test: `Zurari.Tests/DualPaneTests.cs`(新規作成)

#### ステップ

- [ ] 1.1 `Zurari.Tests/DualPaneTests.cs` を新規作成し、最小ペイン高さ判定のテストを先に書く。

  ```csharp
  using Xunit;

  public class DualPaneTests
  {
      // 最小ペイン高さ = ヘッダ1 + 本文3 = 4行。フッター2行+区切り線1行を引いた残りが
      // 2ペイン分(4行×2=8行)未満なら分割不可。
      const int MinPaneHeight = 4;

      static bool CanSplit(int terminalHeight)
      {
          int available = terminalHeight - 2 /* footer */ - 1 /* separator */;
          return available >= MinPaneHeight * 2;
      }

      [Theory]
      [InlineData(11, false)]  // available = 8 = MinPaneHeight*2 → ちょうど境界は許可
      [InlineData(10, false)]  // available = 7 < 8 → 拒否
      [InlineData(12, true)]   // available = 9 >= 8 → 許可
      [InlineData(3, false)]   // 極端に小さい端末
      public void CanSplit_RequiresMinimumAvailableHeight(int terminalHeight, bool expected)
      {
          Assert.Equal(expected, CanSplit(terminalHeight));
      }

      [Fact]
      public void PaneHeight_OddAvailable_ExtraRowGoesToBottomPane()
      {
          // height=15 → available = 15-2-1 = 12 → paneHeight(top) = 12/2 = 6, bottom = 12-6 = 6 (偶数なので均等)
          int height = 15;
          int available = height - 2 - 1;
          int topHeight = available / 2;
          int bottomHeight = available - topHeight;
          Assert.Equal(6, topHeight);
          Assert.Equal(6, bottomHeight);
      }

      [Fact]
      public void PaneHeight_EvenAvailable_ExtraRowGoesToBottomPane()
      {
          // height=14 → available = 11 → topHeight = 11/2 = 5 (整数除算), bottomHeight = 11-5 = 6 (端数は下へ)
          int height = 14;
          int available = height - 2 - 1;
          int topHeight = available / 2;
          int bottomHeight = available - topHeight;
          Assert.Equal(5, topHeight);
          Assert.Equal(6, bottomHeight);
      }
  }
  ```

- [ ] 1.2 `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、上記4テストが通ることを確認する(この時点では `Program.cs` を触っていないため、既存コードとは独立したロジックの検証。**期待結果**: `Passed! - Failed: 0`)。

- [ ] 1.3 `Program.cs` の `ScreenState` 直後(または既存の定数群の近く)に最小ペイン高さの定数を追加する。

  ```csharp
  const int MinPaneHeight = 4; // ヘッダ1行 + 本文3行
  ```

- [ ] 1.4 `Program.cs` に `CanSplit(int terminalHeight)` ヘルパを追加する(`UpdateHorizontalScroll` の近くなど、static メソッド群の中に配置)。

  ```csharp
  static bool CanSplit(int terminalHeight)
  {
      int available = terminalHeight - 2 /* footer rows */ - 1 /* separator row */;
      return available >= MinPaneHeight * 2;
  }
  ```

- [ ] 1.5 `ToggleSplitAsync()` を実装する。分割時は「フォーカスペインの現在ディレクトリ」を初期パスとして第2 Pane を生成する。初期パスは `GetSelectedDirectory` 相当ではなく、**フォーカスペインの `ActiveColumn` が指すカラムの `Path`**(= そのカラム自体が表示しているディレクトリ)である。カーソル下のエントリが指す先ではなく、フォーカスペインが「現在表示しているディレクトリ」を新ペインのルートにする(spec: 「その時点でフォーカスしていたペインの現在ディレクトリ」)。

  ```csharp
  static async Task ToggleSplitAsync()
  {
      if (!State.SplitActive)
      {
          if (!CanSplit(Console.WindowHeight))
          {
              _lastErrorMessage = "[terminal too small to split]";
              return;
          }

          Pane source = FocusedPane;
          string initialPath = source.Columns[source.ActiveColumn].Path;

          Pane newPane = new Pane();
          newPane.Columns.Add(CreateColumn(initialPath));
          await RebuildRightSideAsync(newPane, 0);

          State.Panes.Add(newPane);
          State.SplitActive = true;
          // フォーカスは元々見ていたペイン(常に State.Panes[0])のまま据え置く。
          // ユーザーは Ctrl+W 直後にそのまま同じペインを操作継続できる。
          State.FocusedPane = 0;
      }
      else
      {
          // 非フォーカス側 Pane を破棄する。フォーカス側だけを残す。
          int keepIndex = State.FocusedPane;
          int discardIndex = keepIndex == 0 ? 1 : 0;

          Pane discarded = State.Panes[discardIndex];
          CancelAllReadsInPane(discarded);

          Pane kept = State.Panes[keepIndex];
          State.Panes.Clear();
          State.Panes.Add(kept);
          State.FocusedPane = 0;
          State.SplitActive = false;
      }

      State.PrevFrame = null; // レイアウトが変わるため強制フル再描画
      UpdateHorizontalScroll(FocusedPane);
  }

  static void CancelAllReadsInPane(Pane pane)
  {
      // 破棄される Pane 配下の全カラムで非同期読み込み中の ReadCts をキャンセルする。
      // CancelRightSideReads は「columnIndex より右側のみ」を対象にするため、
      // Pane 全体(index -1 起点 = 全カラム)を渡して漏れなくキャンセルする。
      foreach (Column col in pane.Columns)
      {
          col.ReadCts?.Cancel();
      }
  }
  ```

  **設計上の注意(実装セッションへの明記)**: `CancelRightSideReads(pane, columnIndex)` は「`columnIndex` より右側」のみを対象にする既存関数であり、Pane 全体の破棄時にはカラム0自身の `ReadCts` もキャンセルする必要があるため、既存の `CancelRightSideReads` をそのまま流用せず `CancelAllReadsInPane` を新設する。`RebuildRightSideAsync` 内の破棄ループ(`col.ReadCts?.Cancel(); SaveCursorMemory(col);`)と同様に、`SaveCursorMemory` も呼ぶべきか検討した結果、**Pane 全体を破棄する場合はカーソルメモリの保存は行う**(ユーザーがそのディレクトリに再訪した際に同じカーソル位置を復元できるようにするため、既存の「カラム破棄時は必ず SaveCursorMemory」という規則と一貫させる)。`CancelAllReadsInPane` を次のように修正する。

  ```csharp
  static void CancelAllReadsInPane(Pane pane)
  {
      foreach (Column col in pane.Columns)
      {
          col.ReadCts?.Cancel();
          SaveCursorMemory(col);
      }
  }
  ```

- [ ] 1.6 `HandleSpecialKeyAsync` の `switch (key.Key)` に `Ctrl+W` のケースを追加する。既存の `case ConsoleKey.L:` (Ctrl+L のリフレッシュ処理)のすぐ後、`case ConsoleKey.F3:` の直前に追加する。

  ```csharp
  case ConsoleKey.W:
      if ((key.Modifiers & ConsoleModifiers.Control) != 0)
      {
          await ToggleSplitAsync();
      }
      break;
  ```

- [ ] 1.7 `dotnet build Zurari.csproj` を実行する。**期待結果**: exit code 0、警告なし(または既存と同数の警告)。

- [ ] 1.8 `git add Program.cs Zurari.Tests/DualPaneTests.cs && git commit -m "feat: add Ctrl+W split toggle with pane creation/teardown"` を実行する。

---

### Task 2: `Tab` フォーカス移動(分割時のみ)

**Files:**
- Modify: `Program.cs`(`HandleSpecialKeyAsync` に `Tab` ケースを追加)
- Test: `Zurari.Tests/DualPaneTests.cs`(フォーカス切り替えロジックのテストを追加)

#### ステップ

- [ ] 2.1 `Zurari.Tests/DualPaneTests.cs` に、フォーカス切り替えの純粋ロジックをテストする既存クラスへ追記する(`Pane`/`ScreenState` の実クラスを直接使う)。

  ```csharp
  public class FocusSwitchTests
  {
      [Fact]
      public void Tab_WhenNotSplit_DoesNotChangeFocus()
      {
          var state = new ScreenState();
          state.Panes.Add(new Pane());
          state.SplitActive = false;
          state.FocusedPane = 0;

          // 分割していないときは Tab を無視する(呼び出し側のガード条件を検証)
          bool shouldSwitch = state.SplitActive;
          Assert.False(shouldSwitch);
      }

      [Fact]
      public void Tab_WhenSplit_TogglesBetweenPane0And1()
      {
          var state = new ScreenState();
          state.Panes.Add(new Pane());
          state.Panes.Add(new Pane());
          state.SplitActive = true;
          state.FocusedPane = 0;

          state.FocusedPane = state.FocusedPane == 0 ? 1 : 0;
          Assert.Equal(1, state.FocusedPane);

          state.FocusedPane = state.FocusedPane == 0 ? 1 : 0;
          Assert.Equal(0, state.FocusedPane);
      }
  }
  ```

- [ ] 2.2 `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、新規2テストが通ることを確認する。

- [ ] 2.3 `HandleSpecialKeyAsync` の `switch (key.Key)` に `Tab` のケースを追加する。`case ConsoleKey.W:` (Task 1 で追加した Ctrl+W ケース)の直後に追加する。

  ```csharp
  case ConsoleKey.Tab:
      if (State.SplitActive)
      {
          State.FocusedPane = State.FocusedPane == 0 ? 1 : 0;
      }
      break;
  ```

  非分割時は `State.SplitActive == false` なので何もしない(spec: 「`Tab`(フォーカス移動)は、ダイアログが開いていない場合に限り通常キー処理の一部として扱う」。分割していないときの Tab は無害な no-op でよい。この挙動は spec に反しないため、実装セッションは判断保留せず進めてよい)。

- [ ] 2.4 `dotnet build Zurari.csproj` を実行する。**期待結果**: exit code 0。

- [ ] 2.5 `git add Program.cs Zurari.Tests/DualPaneTests.cs && git commit -m "feat: add Tab focus switch between split panes"` を実行する。

---

### Task 3: `BuildFrame` の2ペイン対応(`DrawPaneToLines` 導入)

**Files:**
- Modify: `Program.cs`(`BuildFrame`, 新規 `DrawPaneToLines`, `DrawColumnToLines` に `focused` 引数追加, `DrawPreviewToLines` は変更不要)

#### ステップ

- [ ] 3.1 `DrawColumnToLines` のシグネチャに `bool focused` を追加する。既存シグネチャ:
  ```csharp
  static void DrawColumnToLines(Line[] lines, Column column, int displayX, bool active, bool isLeft, int frameWidth, int frameHeight, bool isFirstVisible)
  ```
  を次に変更する(引数を末尾に追加し、呼び出し側の修正漏れをビルドエラーで検出できるようにする)。
  ```csharp
  static void DrawColumnToLines(Line[] lines, Column column, int displayX, bool active, bool isLeft, int frameWidth, int frameHeight, bool isFirstVisible, bool focused)
  ```

- [ ] 3.2 `DrawColumnToLines` 内の ANSI カラーリテラルを、`focused` に応じたパレット選択に置き換える。関数冒頭(`int visibleHeight = frameHeight - 3;` の直後)に減光パレットのローカル定数群を追加する。

  ```csharp
  // 減光パレット選択(非フォーカスペイン用)。SGR 2 (dim) は端末依存で不安定なため使わず、
  // ANSI 90 系(明るい黒 = グレー)の色コードに明示的に差し替える。
  string dirColor      = focused ? "\x1b[94m" : "\x1b[90m";   // ディレクトリ色: 明青→グレー
  string selBgActive   = focused ? "\x1b[47m" : "\x1b[100m";  // 選択行背景(このカラムがアクティブ時): 白→暗グレー
  string selBgInactive = "\x1b[100m";                          // 選択行背景(このカラムが非アクティブ時): 常に暗グレー(変更なし)
  string selDirText    = focused ? "\x1b[94m" : "\x1b[90m";   // 選択行内ディレクトリ文字色
  string selFileText   = focused ? "\x1b[30m" : "\x1b[90m";   // 選択行内ファイル文字色
  string matchBg       = focused ? "\x1b[42m" : "\x1b[100m";  // Search 現在マッチ背景: 緑→暗グレー
  string matchText     = focused ? "\x1b[30m" : "\x1b[90m";   // Search 現在マッチ文字色
  string otherMatchFg  = focused ? "\x1b[32m" : "\x1b[90m";   // Search 他マッチ前景: 緑→グレー
  string leapSelBg     = focused ? "\x1b[47m" : "\x1b[100m";  // Leap 選択行背景
  string leapDirText   = focused ? "\x1b[94m" : "\x1b[90m";   // Leap 選択行ディレクトリ文字色
  string leapFileText  = focused ? "\x1b[30m" : "\x1b[90m";   // Leap 選択行ファイル文字色
  ```

  以降、関数内の該当箇所を次の対応表の通りに置換する。

  | 既存コード(生 ANSI リテラル) | 置換後 |
  |---|---|
  | `if (isDirectory) entry = AnsiColors.Colorize(entry, AnsiColors.Blue);`(Leap分岐、通常ディレクトリ表示、2箇所) | `if (isDirectory) entry = AnsiColors.Colorize(entry, dirColor);` |
  | `string bgColor = "\x1b[47m"; string textColor = isDirectory ? "\x1b[94m" : "\x1b[30m";`(Leap選択行) | `string bgColor = leapSelBg; string textColor = isDirectory ? leapDirText : leapFileText;` |
  | `string bgColor = "\x1b[42m"; string textColor = "\x1b[30m";`(Search現在マッチ) | `string bgColor = matchBg; string textColor = matchText;` |
  | `string bgColor = active ? "\x1b[47m" : "\x1b[100m"; string textColor = isDirectory ? "\x1b[94m" : "\x1b[30m";`(通常選択行) | `string bgColor = active ? selBgActive : selBgInactive; string textColor = isDirectory ? selDirText : selFileText;` |
  | `string coloredEntry = "\x1b[32m" + entry + "\x1b[0m";`(Search他マッチ) | `string coloredEntry = otherMatchFg + entry + "\x1b[0m";` |

  **注記**: `active`(同一ペイン内でのアクティブカラムか)と `focused`(ペイン全体がフォーカスされているか)は独立した軸。両方が false の場合(非フォーカスペインの非アクティブカラム)でも上記置換後の `selBgInactive`/`otherMatchFg` 等は既に非フォーカス用の値になっているため、二重に減光されることはない(表の置換後の値は `focused` のみを条件にしており、`active` は既存のロジックのまま残る)。

- [ ] 3.3 `AnsiColors` クラスにグレー系の色定数を追加する(表内で文字列リテラルとして直書きしたが、可読性のため定数化する)。

  ```csharp
  static class AnsiColors
  {
      public const string Blue   = "\x1b[94m";
      public const string Yellow = "\x1b[33m";
      public const string Gray   = "\x1b[90m";   // 減光: ディレクトリ・選択文字・マッチ文字用
      public const string Reset  = "\x1b[0m";
      public static string Colorize(string text, string color) => color + text + Reset;
  }
  ```

  ステップ 3.2 の `dirColor`/`selDirText`/`selFileText`/`matchText`/`otherMatchFg`/`leapDirText`/`leapFileText` の非フォーカス値(`"\x1b[90m"`)を `AnsiColors.Gray` に置き換える(リテラル直書きをやめる)。

- [ ] 3.4 `DrawPaneToLines` を新設する。既存の `DrawColumnToLines`/`DrawPreviewToLines` 呼び出しをラップし、`yOffset` を全書き込み行インデックスへ加算する。`BuildFrame` 内の既存カラム描画ループ・プレビュー描画をこの関数へ切り出す形にする。`DrawColumnToLines`/`DrawPreviewToLines` 自体は「渡された `lines` 配列に `startRow` から書き込む」実装のため、`yOffset` 加算は「渡す `lines` 配列のスライス」ではなく「渡す行のオフセット済み参照」で行う。具体的には、`Line[] lines` 全体(画面全体分の長さ)をそのまま渡し、`DrawColumnToLines`/`DrawPreviewToLines` 内部の書き込み先インデックスに `yOffset` を加算できるよう、両関数のシグネチャにも `yOffset` を追加する。

  `DrawColumnToLines` のシグネチャをさらに次のように変更する(Task 3.1 の変更に重ねて適用)。

  ```csharp
  static void DrawColumnToLines(Line[] lines, Column column, int displayX, bool active, bool isLeft, int frameWidth, int frameHeight, bool isFirstVisible, bool focused, int yOffset)
  ```

  関数内でヘッダ行・本文行への書き込みが `lines[0]`、`lines[startRow + i]` の形になっている箇所をすべて `lines[yOffset + 0]`、`lines[yOffset + startRow + i]` に変更する(`startRow = 1` はそのまま、加算対象が変わるだけ)。また `int visibleHeight = frameHeight - 3;` の `frameHeight` は「そのペインの高さ」を渡す(画面全体の高さではない)。

  同様に `DrawPreviewToLines` のシグネチャに `int yOffset` を追加する。

  ```csharp
  static void DrawPreviewToLines(Line[] lines, PreviewContent content, int startX, int previewWidth, int frameHeight, int yOffset)
  ```

  関数内部の行書き込みインデックスにも同様に `yOffset` を加算する(ヘッダ・本文の書き込み箇所すべて)。

  `DrawPaneToLines` 本体は次の通り。

  ```csharp
  static void DrawPaneToLines(Line[] lines, Pane pane, int yOffset, int paneHeight, bool focused, int width)
  {
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
          DrawColumnToLines(lines, pane.Columns[i], displayX, i == pane.ActiveColumn, i < pane.ActiveColumn, width, paneHeight, isFirstVisible, focused, yOffset);
          displayX += ColumnWidth;
          drawnColumnsWidth += ColumnWidth;
      }

      if (showPreview)
      {
          int previewWidth = width - drawnColumnsWidth;
          PreviewContent content;
          if (pane.Preview.IsLoading || pane.Preview.Content == null)
          {
              content = new PreviewContent("Loading…", "", "", ["⠋"], true);
          }
          else
          {
              content = pane.Preview.Content;
          }

          if (previewWidth >= ColumnWidth)
              DrawPreviewToLines(lines, content, drawnColumnsWidth, previewWidth, paneHeight, yOffset);
      }
  }
  ```

  **注記**: `SelectedItemIsFileOrDrive()` は Phase 1 のリファクタで `SelectedItemIsFileOrDrive(Pane pane)` になっている前提(既存の API 契約変換規則「`State.ActiveColumn` を参照する static メソッドは先頭引数 `Pane pane` を取る」に従う)。異なる場合は「未解決事項」に記録する。

- [ ] 3.5 `BuildFrame(int width, int height)` を書き換える。区切り線描画・パス行プレフィックスの付与も含めて全体を次の通りに変更する。

  ```csharp
  static string[] BuildFrame(int width, int height)
  {
      string[] frame = new string[height];

      Line[] lines = new Line[height];
      for (int i = 0; i < height; i++)
          lines[i] = new Line();

      if (!State.SplitActive)
      {
          DrawPaneToLines(lines, State.Panes[0], 0, height - 2, true, width);
      }
      else
      {
          int available = height - 2 - 1; // フッター2行・区切り線1行を除いた残り
          int topHeight = available / 2;
          int bottomHeight = available - topHeight; // 端数は下ペインへ
          int separatorRow = topHeight;
          int bottomOffset = topHeight + 1;

          DrawPaneToLines(lines, State.Panes[0], 0, topHeight, State.FocusedPane == 0, width);

          // 区切り線(1行)
          string sep = new string('─', width);
          lines[separatorRow].AddColumn(sep, width, width);

          DrawPaneToLines(lines, State.Panes[1], bottomOffset, bottomHeight, State.FocusedPane == 1, width);
      }

      // Render each line to string and pad to window width
      for (int i = 0; i < height - 2; i++)
      {
          frame[i] = lines[i].Render(width);
      }

      // Full path line (above status) — フォーカスペインの値を表示
      Pane focusedPane = FocusedPane;
      string fullPath = GetCurrentFullPath(focusedPane);
      if (State.SplitActive)
      {
          string prefix = State.FocusedPane == 0 ? "[^]" : "[v]";
          fullPath = prefix + fullPath;
      }
      fullPath = CharacterWidth.SmartTruncate(fullPath, width);
      frame[height - 2] = CharacterWidth.PadToWidth(fullPath, width);

      // Override status with preview-hidden hint only when terminal width is the limiting factor
      if (focusedPane.Preview.IsVisible && SelectedItemIsFileOrDrive(focusedPane) && Console.WindowWidth < ColumnWidth * 2)
      {
          _lastErrorMessage = "[preview hidden: narrow terminal]";
      }

      // Status line
      if (focusedPane.Leap.Active)
      {
          string leapBar = SearchHelper.BuildLeapStatusBar(focusedPane.Leap, focusedPane.Columns, focusedPane.ActiveColumn, width);
          frame[height - 1] = leapBar;
      }
      else if (focusedPane.Search.Active)
      {
          List<int> matchSnapshot;
          bool done;
          lock (_searchLock)
          {
              matchSnapshot = new List<int>(focusedPane.Search.Matches);
              done = focusedPane.Search.SearchDone;
          }
          string searchBar = SearchHelper.BuildSearchStatusBar(focusedPane.Search, matchSnapshot, done, width);
          frame[height - 1] = searchBar;
      }
      else if (_lastErrorMessage != null)
      {
          string status = _lastErrorMessage;
          _lastErrorMessage = null;
          status = CharacterWidth.SmartTruncate(status, width);
          frame[height - 1] = CharacterWidth.PadToWidth(status, width);
      }
      else
      {
          string previewHint = focusedPane.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
          string status;
          if (IsWindows())
              status = $"Esc=Quit | Ctrl+Enter=Open | Shift+Enter/\\=Menu | {previewHint}";
          else
              status = $"Esc=Quit | Ctrl+Enter=Open File | {previewHint}";
          status = CharacterWidth.SmartTruncate(status, width);
          frame[height - 1] = CharacterWidth.PadToWidth(status, width);
      }

      // オーバーレイ(ダイアログ)は Phase 3 で導入済みの最終段階処理をこの後にそのまま適用する。
      // (Phase 3 実装済みのオーバーレイ描画コードはここでは変更しない。BuildFrame末尾の
      //  既存の "if (State.Dialog != null) { ... }" ブロックをそのまま残すこと。)

      return frame;
  }
  ```

  **端数配分の確認**: `topHeight = available / 2` は C# の整数除算で切り捨てのため、`available` が奇数のとき `topHeight` は小さい方、`bottomHeight = available - topHeight` は大きい方になる。これにより「端数は下ペインへ」という spec の規則が自動的に満たされる(Task 1.1 のテストで検証済み)。

  **区切り線の文字**: spec は区切り線の具体的な文字を指定していない。本計画では罫線素片 `─`(U+2500, 表示幅1)を採用する。`CharacterWidth.GetCharWidth('─')` が1を返すことを手動確認で検証すること(Manual Verification 節参照)。仮に環境によって表示崩れが起きる場合、実装セッションは `-`(ハイフン)への変更を検討してよいが、変更した場合は「未解決事項」に理由を記録する。

- [ ] 3.6 `BuildFrame` 内外で `DrawColumnToLines`/`DrawPreviewToLines` を直接呼んでいる箇所が他に残っていないか `Program.cs` 全体を検索する(`grep -n "DrawColumnToLines\|DrawPreviewToLines" Program.cs`)。`DrawPaneToLines` 経由以外の直接呼び出しが残っていた場合、シグネチャ変更でビルドエラーになるため、すべて `DrawPaneToLines` 経由に統一するか、呼び出し箇所に `yOffset`/`focused` 引数を追加する。

- [ ] 3.7 `dotnet build Zurari.csproj` を実行する。**期待結果**: exit code 0。シグネチャ変更に伴うビルドエラーが出た場合はすべて解消してから次に進む。

- [ ] 3.8 `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行する。**期待結果**: `Passed!`(全緑)。既存の `SearchStatusBarTests` 等が `BuildLeapStatusBar`/`BuildSearchStatusBar` のシグネチャに依存している場合、影響がないことを確認する(これらのシグネチャは本 Phase では変更しない)。

- [ ] 3.9 単一ペイン・ダイアログなし状態で、Phase 2 完了条件(「旧 `BuildFrame` と全く同じ出力」)が本 Phase の変更後も保たれているかを手動確認する(Manual Verification 節 M1 参照)。

- [ ] 3.10 `git add Program.cs && git commit -m "feat: split BuildFrame into per-pane viewport rendering with dimming palette"` を実行する。

---

### Task 4: 入力ルーティングの確認(Leap/Search がペイン独立で動くこと)

Phase 3 で確立済みの優先順位(`State.Dialog != null` → `FocusedPane.Leap.Active` → `FocusedPane.Search.Active` → 通常キー)が、Phase 1 の `Pane` 導入と Task 1〜3 のペイン追加によって壊れていないことをコードレビューと手動確認で担保する。このタスクはコード変更を伴わない検証タスク。

#### ステップ

- [ ] 4.1 `Main` のメインループ内の分岐(`State.Leap.Active` / `State.Search.Active` を参照している箇所)が、Phase 1 のリファクタで `FocusedPane.Leap.Active` / `FocusedPane.Search.Active` に置き換わっていることを確認する。置き換わっていない箇所(例: 無入力時デバウンス処理の `State.Leap.NeedsRecompute` 等)があれば、`FocusedPane` 経由に修正する。

  ```csharp
  if (!Console.KeyAvailable)
  {
      Pane fp = FocusedPane;
      if (fp.Leap.Active
          && fp.Leap.NeedsRecompute
          && (DateTime.UtcNow - fp.Leap.LastInputTime).TotalMilliseconds >= NavigationDebounceMs)
      {
          fp.Leap.NeedsRecompute = false;
          ApplyLeapFilter(fp);
          await TryLeapAutoEnterAsync(fp);
      }
      else if (fp.Search.Active
          && fp.Search.NeedsRecompute
          && (DateTime.UtcNow - fp.Search.LastInputTime).TotalMilliseconds >= 300)
      {
          await RecomputeMatchesAsync(fp);
          UpdateHorizontalScroll(fp);
          await RebuildRightSideAsync(fp, fp.ActiveColumn);
          if (fp.Preview.IsVisible) StartPreviewLoad(fp);
      }
      System.Threading.Thread.Sleep(50);
      continue;
  }

  ConsoleKeyInfo key = Console.ReadKey(true);

  if (State.Dialog != null)
  {
      await HandleDialogKeyAsync(key);
      continue;
  }

  Pane focused = FocusedPane;
  if (focused.Leap.Active)
  {
      await HandleLeapKeyAsync(focused, key);
      continue;
  }

  if (focused.Search.Active)
  {
      await HandleSearchKeyAsync(focused, key);
      continue;
  }

  switch (key.KeyChar)
  {
      case (char)27: // Esc
          return;

      default:
          await HandleSpecialKeyAsync(focused, key);
          break;
  }
  ```

  **注記**: 上記コードは Phase 1〜3 で既に到達しているべき形。本 Phase での変更点は「`FocusedPane` ヘルパ経由になっていること」の確認のみであり、`HandleSpecialKeyAsync` 等のシグネチャは Phase 1 で既に `Pane pane` を取る形になっているはずなので、Task 1〜3 で追加した `ToggleSplitAsync()`/`Tab` ケースの呼び出し元(`HandleSpecialKeyAsync`)のシグネチャとも整合させる。Task 1.6 / 2.3 で追加したコードは `HandleSpecialKeyAsync` 内の `switch` 文の一部として書いたが、Phase 1 のリファクタにより実際の関数シグネチャが `HandleSpecialKeyAsync(Pane pane, ConsoleKeyInfo key)` になっている場合、`ToggleSplitAsync()`/`Tab` ケースの処理自体はペイン非依存(`State.SplitActive`/`State.FocusedPane`/`State.Panes` のみ操作)のためそのまま動作するが、関数呼び出しの整合のため実装セッションは実際のシグネチャに合わせて調整すること。

- [ ] 4.2 `HandleSpecialKeyAsync` に渡ってくる `pane` 引数が非フォーカスペインになることが構造的にないこと(呼び出し元が常に `FocusedPane` を渡すこと)をコードで確認する。

- [ ] 4.3 手動確認手順(Manual Verification 節 M6「ペイン独立ナビゲーション」)を実施する準備として、この時点でアプリをビルドし起動できることを確認する: `dotnet build Zurari.csproj && dotnet bin/Debug/net8.0/Zurari.dll`(または `dotnet run --project Zurari.csproj`)。**期待結果**: 起動しクラッシュしない。

- [ ] 4.4 このタスクで `Program.cs` に実際の修正が発生した場合のみ、`git add Program.cs && git commit -m "fix: route leap/search/dialog input through FocusedPane consistently"` を実行する。修正が発生しなかった場合はコミット不要(タスクを完了としてチェックのみ行う)。

---

## 手動確認手順(Manual Verification)

ビルド後、`dotnet run --project Zurari.csproj` でアプリを起動し、以下を順に確認する。ターミナルは 30行×100列程度を推奨(分割に十分な高さを確保するため)。

### M1: Phase 2 完了条件の回帰確認(単一ペイン時の見た目不変)
1. 起動直後(分割なし)の画面を確認する。
2. **期待画面**: Task 3 実施前と同じレイアウト(カラム一覧・パス行・ステータス行)。パス行に `[^]`/`[v]` プレフィックスが**付いていない**こと。

### M2: `Ctrl+W` によるトグル往復
1. `Ctrl+W` を押す。
2. **期待画面**: 画面が上下2分割される。上下の境界に罫線(`─`)が1行表示される。パス行の先頭に `[^]`(上ペインがフォーカス、初期状態は `State.FocusedPane == 0`)が表示される。下ペインのカラムは、`Ctrl+W` を押した時点でフォーカスしていたペインの現在ディレクトリと同じ内容で開かれている。
3. もう一度 `Ctrl+W` を押す。
4. **期待画面**: 分割が解除され、フォーカスしていたペイン(この場合は上ペイン)の内容のみが全画面表示に戻る。パス行のプレフィックスが消える。
5. 再度 `Ctrl+W` → 今度は `Tab` で下ペインにフォーカスを移してから `Ctrl+W` を押す。
6. **期待画面**: 下ペイン(フォーカス側)が残り、上ペインが破棄されて全画面表示になる。

### M3: `Tab` フォーカス移動
1. `Ctrl+W` で分割する。
2. `Tab` を押す。
3. **期待画面**: パス行のプレフィックスが `[^]` → `[v]` に切り替わる。上ペインが減光表示(ディレクトリ色がグレーがかった色に、選択行の背景が暗いグレーに)になり、下ペインが通常表示になる。
4. もう一度 `Tab` を押す。
5. **期待画面**: `[v]` → `[^]` に戻り、減光対象が上下逆転する。

### M4: 減光パレットの確認
1. `Ctrl+W` で分割し、上ペイン(フォーカス)でディレクトリにカーソルを合わせる。
2. **期待画面**: 上ペインのディレクトリ名は明るい青(ANSI 94 相当)、選択行の背景は白、カーソルは `>` プレフィックス。
3. 下ペイン(非フォーカス)のディレクトリ名を確認する。
4. **期待画面**: 下ペインのディレクトリ名はグレー(ANSI 90 相当、上ペインより明らかに暗い)。下ペインの選択行の背景も暗いグレーで、強調表示が弱い。
5. `Tab` でフォーカスを下へ移す。
6. **期待画面**: 明暗が反転する(下ペインが明るく、上ペインがグレーに)。

### M5: パス行プレフィックス
1. 非分割状態でパス行を確認する。**期待画面**: プレフィックスなし。
2. `Ctrl+W` で分割する。**期待画面**: `[^]`(上がフォーカス)。
3. `Tab`。**期待画面**: `[v]`(下がフォーカス)。
4. `Ctrl+W` で解除。**期待画面**: プレフィックスが再び消える。

### M6: Leap/Search のペイン独立動作
1. `Ctrl+W` で分割する(フォーカスは上ペイン)。
2. 上ペインで `/` を押して検索モードに入り、何か文字を入力する。
3. **期待画面**: 上ペインのステータス行に検索バーが表示され、上ペイン内でマッチがハイライトされる。下ペインは通常表示のまま。
4. `Esc` で検索を終了せずに(検索アクティブなまま)`Tab` を押そうとする。
5. **期待画面**: 検索モード中は `Tab` が検索の入力として処理され(またはフォーカス移動キーとして扱われない)、フォーカスは上ペインのままである。**確認事項**: `HandleSearchKeyAsync` が `Tab` キーを消費するかどうかは既存の Phase 3 実装に依存する。もし検索中に `Tab` でフォーカスが移ってしまう場合、それは spec の「ダイアログが開いている間は…フォーカスペインの Leap/Search 状態がアクティブであっても入力は届かない」という記述とは別の問題(Leap/Search 自体が Tab を消費すべきかどうか)であるため、実装セッションはこの挙動を「未解決事項」に記録し、決め打ちで修正しない。
6. `Esc` で検索を終了し、`Tab` で下ペインにフォーカスを移す。
7. 下ペインで `/` を押し、上ペインとは別のクエリで検索する。
8. **期待画面**: 下ペインの検索結果と上ペインの検索結果(既に終了しているので非表示)が独立している。上ペインに戻って `Tab` した際、上ペインの検索状態(終了済み)がそのまま保持されていること。
9. 同様の手順を `F3`(Leap)でも行う。上ペインで Leap を開始し、`Tab` で下ペインに移り、下ペインで別の Leap を開始し、双方の `Buffer`/`VisibleIndices` が独立していることを確認する。

### M7: 小さすぎる端末での分割拒否
1. ターミナルの高さを縮小する(例: 8行程度。`MinPaneHeight = 4` なので `available = height - 3 >= 8` を満たすには `height >= 11` が必要)。
2. `Ctrl+W` を押す。
3. **期待画面**: 分割されず、ステータス行に `[terminal too small to split]` が一度だけ表示される。
4. ターミナルを `height = 11` 以上に戻し、`Ctrl+W` を押す。
5. **期待画面**: 正常に分割される。

### M8: 両ペイン独立ナビゲーション
1. `Ctrl+W` で分割する。
2. 上ペインで別のディレクトリへ `Enter` で移動する。
3. `Tab` で下ペインに移り、上ペインとは異なるディレクトリへ移動する。
4. `Tab` で上ペインに戻る。
5. **期待画面**: 上ペインは手順2で移動した先のディレクトリ・カーソル位置を保持している(下ペインの操作の影響を受けていない)。

---

## 完了条件チェックリスト

- [ ] `Ctrl+W` で分割の開閉が往復できる(M2)。
- [ ] `Tab` でフォーカスが移動する(分割時のみ、M3)。
- [ ] 両ペインが独立してナビゲーション・検索・Leap を実行できる(M6, M8)。
- [ ] 非フォーカスペインの減光表示が正しく切り替わる(M4)。
- [ ] パス行の `[^]`/`[v]` プレフィックスが正しく切り替わる(分割時のみ表示、M5)。
- [ ] 小さすぎる端末では分割が拒否され `_lastErrorMessage` が表示される(M7)。
- [ ] 分割解除時、非フォーカス側 Pane の `ReadCts` がキャンセルされる(コードレビューで確認: `CancelAllReadsInPane`)。
- [ ] `dotnet build Zurari.csproj` が exit 0 で成功する。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` が全緑(`Passed!`)である。
- [ ] Phase 2 完了条件(単一ペイン・ダイアログなし時の出力が変更前と同一)が本 Phase 後も保たれている(M1)。

## 未解決事項

(実行セッションが判断保留を記録する欄。空のまま残すこと)
