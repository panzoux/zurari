# Phase 2: ビューポート描画 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `BuildFrame` を「ペイン矩形描画 → 共有フッター → オーバーレイ(空フック)」の3層合成に分解し、各ペインの描画関数が `yOffset` を受け取れるようにする。単一ペイン・ダイアログなしの状態で、変更前と画面出力(端末に書き込まれるバイト列)が完全に同一であることを保つ。
**Architecture:** Phase 1 で導入済みの `Pane`(`Columns`/`ActiveColumn`/`HorizontalScroll`/`Search`/`Leap`/`Preview` を束ねる)を土台に、既存の `DrawColumnToLines`/`DrawPreviewToLines` に `yOffset` 引数を追加し、両者を呼び出す新規ラッパー `DrawPaneToLines(Line[] lines, Pane pane, int yOffset, int height, bool focused, int width)` を新設する。`BuildFrame` は「ペイン矩形描画(`DrawPaneToLines` を高さ `height-2`・`yOffset=0` で1回呼ぶだけ)→ 共有フッター(パス行+ステータス行)→ オーバーレイ(Phase 3 まで何もしない空フック)」の3段に分解する。この Phase では `Panes.Count` は常に1、`SplitActive` は常に false、`focused` は常に true として扱う(分割自体は Phase 5)。減光パレットの実装もこの Phase では行わない(Phase 5)。
**Tech Stack:** C# / .NET 8, xUnit(Zurari.Tests), 自前 TUI(外部 TUI フレームワーク禁止)

---

## 前提条件(開始前に検証)

- [x] `git log --oneline -10` を実行し、Phase 1(Pane 抽出リファクタ)の完了コミットが存在することを確認する。(確認済み: `d33ccac` 他)
- [x] `dotnet build Zurari.csproj` を実行し、exit code 0 で成功することを確認する。(確認済み: exit 0)
- [x] `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、`Passed!`(全緑・失敗0件)であることを確認する。(確認済み: 64/64)
- [x] `Program.cs` を開き、次を `grep` で確認する。
  ```
  grep -n "sealed class Pane\|sealed class ScreenState\|static Pane FocusedPane" Program.cs
  ```
  期待: `sealed class Pane` が `Columns`/`ActiveColumn`/`HorizontalScroll`/`Search`/`Leap`/`Preview` を持つこと、`sealed class ScreenState` が `PrevFrame`/`PrevWidth`/`PrevHeight`/`Panes`(`List<Pane>`)/`FocusedPane`(`int`)/`SplitActive`(`bool`)を持つこと、`static Pane FocusedPane => State.Panes[State.FocusedPane];` ヘルパが存在すること。一致しない場合、実装セッションは差異を「未解決事項」に記録して停止する(このヘルパが無い場合のみ、Task 1 の最初のステップとして追加してよい。これは spec の API 契約に明記された固定ヘルパであり判断を要しないため)。(確認済み: 完全一致。`DialogState`/`State.Dialog` も宣言のみ既に存在していたが、これは Phase 3 の先行スタブであり本 Phase の前提と矛盾しない。)
- [x] `Program.cs` の `BuildFrame`/`DrawColumnToLines`/`DrawPreviewToLines`/`Draw` を実際に開き、下記「既存コードの前提」節のシグネチャ・行構造と一致していることを確認する。Phase 1 のリファクタで `Columns`/`State.ActiveColumn`/`State.HorizontalScroll`/`State.Search`/`State.Leap`/`State.Preview` を参照していた箇所が `pane.Columns`/`pane.ActiveColumn`/`pane.HorizontalScroll`/`pane.Search`/`pane.Leap`/`pane.Preview`(先頭引数 `Pane pane` 経由)に置き換わっているはずである。置き換わっていない場合、実装セッションはこの Phase の変更対象箇所についてのみ機械的に読み替えて進めてよい(コンパイルが通る書き方の選択の範囲)。ただし関数の**責務・呼び出し順序・行インデックス計算**が下記と異なる場合は「未解決事項」に記録すること。(確認済み: シグネチャ・構造が完全一致。`_framesBuilt++`/`_missingEntryMessage` 処理という Phase 1 後に追加された既存機能があったが、責務・呼び出し順序に影響しないため未解決事項扱いせず、そのまま3層構造に持ち越した。)

この計画と spec に書かれていない判断が必要になったら、自分で決めずに「未解決事項」に記録してそのステップを保留し、次の独立したタスクに進むか停止すること。

---

## 既存コードの前提(Phase 1 完了後の Program.cs、このPhaseが変更する実コード)

`sealed class Line`(`Program.cs` 冒頭付近、`AddColumn(string content, int displayWidth, int columnWidth)` / `Render(int targetWidth)`)は変更しない。行バッファへの「列(カラム or プレビュー)の書き込み」を担う既存の抽象であり、この Phase はこれを呼ぶ側の行インデックス計算だけを変える。

Phase 1 完了後、以下の4関数は次のシグネチャ・構造を持つ想定(引数名は Phase 1 のリファクタ結果に合わせて `pane` を主語に統一されている前提)。

```csharp
static void Draw()
{
    int width = Console.WindowWidth;
    int height = Console.WindowHeight;

    string[] currentFrame = BuildFrame(width, height);

    if (State.PrevFrame == null)
    {
        Console.Clear();
        for (int i = 0; i < currentFrame.Length; i++)
            WriteLineAt(0, i, currentFrame[i]);
    }
    else
    {
        for (int i = 0; i < currentFrame.Length; i++)
        {
            if (State.PrevFrame[i] != currentFrame[i])
                WriteLineAt(0, i, currentFrame[i]);
        }
    }

    State.PrevFrame = currentFrame;
}

static string[] BuildFrame(int width, int height)
{
    string[] frame = new string[height];
    Line[] lines = new Line[height];
    for (int i = 0; i < height; i++)
        lines[i] = new Line();

    Pane pane = FocusedPane;  // Phase 1 完了後は単一ペイン運用でも FocusedPane 経由

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

    if (showPreview)
    {
        int previewWidth = width - drawnColumnsWidth;
        PreviewContent content;
        if (pane.Preview.IsLoading || pane.Preview.Content == null)
            content = new PreviewContent("Loading…", "", "", ["⠋"], true);
        else
            content = pane.Preview.Content;

        if (previewWidth >= ColumnWidth)
            DrawPreviewToLines(lines, content, drawnColumnsWidth, previewWidth, height);
    }

    for (int i = 0; i < height - 2; i++)
        frame[i] = lines[i].Render(width);

    string fullPath = GetCurrentFullPath(pane);
    fullPath = CharacterWidth.SmartTruncate(fullPath, width);
    frame[height - 2] = CharacterWidth.PadToWidth(fullPath, width);

    if (pane.Preview.IsVisible && SelectedItemIsFileOrDrive(pane) && Console.WindowWidth < ColumnWidth * 2)
        _lastErrorMessage = "[preview hidden: narrow terminal]";

    if (pane.Leap.Active)
    {
        frame[height - 1] = SearchHelper.BuildLeapStatusBar(pane.Leap, pane.Columns, pane.ActiveColumn, width);
    }
    else if (pane.Search.Active)
    {
        List<int> matchSnapshot;
        bool done;
        lock (_searchLock)
        {
            matchSnapshot = new List<int>(pane.Search.Matches);
            done = pane.Search.SearchDone;
        }
        frame[height - 1] = SearchHelper.BuildSearchStatusBar(pane.Search, matchSnapshot, done, width);
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
        string previewHint = pane.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
        string status;
        if (IsWindows())
            status = $"Esc=Quit | Ctrl+Enter=Open | Shift+Enter/\\=Menu | {previewHint}";
        else
            status = $"Esc=Quit | Ctrl+Enter=Open File | {previewHint}";
        status = CharacterWidth.SmartTruncate(status, width);
        frame[height - 1] = CharacterWidth.PadToWidth(status, width);
    }

    return frame;
}

static void DrawColumnToLines(Line[] lines, Column column, int displayX, bool active, bool isLeft, int frameWidth, int frameHeight, bool isFirstVisible)
{
    int visibleHeight = frameHeight - 3;  // Reserve 3 rows: header + fullpath + status
    // ... (本文省略。書き込み先は lines[0](ヘッダ)、lines[startRow + i](本文、startRow = 1)の絶対インデックス)
}

static void DrawPreviewToLines(Line[] lines, PreviewContent content, int startX, int previewWidth, int frameHeight)
{
    int visibleHeight = frameHeight - 3;
    // ... (本文省略。書き込み先は lines[0]〜lines[visibleHeight] の絶対インデックス)
}
```

**罠(この Phase で必ず潰す前提の誤り)**: `DrawColumnToLines`/`DrawPreviewToLines` はどちらも `frameHeight` を受け取り、内部で `visibleHeight = frameHeight - 3` として**その値からスクロール可能な行数を逆算**している。これは「画面全体の高さから、ヘッダ1行+フルパス行+ステータス行の3行を引く」という計算であり、暗黙に「この関数は常に画面全体を描画している」という前提に依存している。ペインが画面の一部矩形(高さ `paneHeight < height`)しか描画しない場合、この関数に渡すべきなのは**画面全体の高さ `height` ではなく、そのペインに割り当てられた矩形の高さ `paneHeight`** である。混同すると、単一ペイン時ですら `frameHeight` の意味が変わってしまい見た目が壊れる。この Phase では `paneHeight = height - 2`(フッター2行を除いた残り全部)を渡すことで、この罠を踏まずに済む形にする。

`DrawColumnToLines` 内で `lines[0]`(ヘッダ)や `lines[startRow + i]`(本文、`startRow = 1`)への書き込みは**画面全体の `lines` 配列に対する絶対インデックス**であり、ペインの矩形が `yOffset` 分だけ下にずれる Phase 5 以降ではこのままでは正しい位置に書けない。この Phase 2 では書き込み先インデックスに `yOffset` を加算できるよう両関数へ `yOffset` 引数を追加する(単一ペインでは `yOffset = 0` を渡すため、加算後も見た目は変わらない)。同様に `DrawPreviewToLines` 内の `lines[0]`〜`lines[visibleHeight]` への書き込みにも同じ加算を行う。

---

## 描画設計(3層合成、この Phase の到達点)

1. **ペイン矩形描画** — `DrawPaneToLines(Line[] lines, Pane pane, int yOffset, int height, bool focused, int width)` を新設し、`DrawColumnToLines`/`DrawPreviewToLines` の呼び出しをラップする。この Phase では `DrawPaneToLines` を `yOffset = 0`, `height = height - 2`, `focused = true` の固定引数で1回だけ呼ぶ(`Panes[0]` に対して)。
2. **共有フッター** — パス行(`frame[height - 2]`)とステータス行(`frame[height - 1]`)。この Phase では単一ペイン専用の内容のまま(`[^]`/`[v]` プレフィックスは Phase 5 で追加、それまでは何も付けない)。
3. **オーバーレイ** — `State.Dialog`(Phase 3 で導入)が非 null なら中央ボックスを上書きする層。この Phase では `DialogState` 自体がまだ存在しないため、**何もしない空フックとして関数の形だけ用意する**(呼び出し箇所を用意し、中身は「no-op」であることをコメントで明記する)。

---

### Task 1: `DrawColumnToLines` / `DrawPreviewToLines` に `yOffset` を追加する

**Files:**
- Modify: `Program.cs`(`DrawColumnToLines`, `DrawPreviewToLines`)

#### ステップ

- [x] 1.1 `Program.cs` で `DrawColumnToLines` の現在のシグネチャを確認する。

  ```
  grep -n "static void DrawColumnToLines" Program.cs
  ```

  Phase 1 完了後の想定シグネチャ:
  ```csharp
  static void DrawColumnToLines(Line[] lines, Column column, int displayX, bool active, bool isLeft, int frameWidth, int frameHeight, bool isFirstVisible)
  ```

- [x] 1.2 シグネチャの末尾に `int yOffset` を追加する(既存の呼び出し箇所すべてがビルドエラーになり、修正漏れを機械的に検出できるようにするため)。

  ```csharp
  static void DrawColumnToLines(Line[] lines, Column column, int displayX, bool active, bool isLeft, int frameWidth, int frameHeight, bool isFirstVisible, int yOffset)
  ```

- [x] 1.3 関数本文中、`lines[` で始まる書き込み箇所をすべて `yOffset` 加算に変更する。対象は次の2パターン(関数内に複数回出現する)。

  - `lines[0].AddColumn(header, headerDisplayWidth, ColumnWidth);` → `lines[yOffset + 0].AddColumn(header, headerDisplayWidth, ColumnWidth);`
  - `lines[startRow + i].AddColumn(...)`(Leap分岐・通常分岐の両方、複数箇所)→ `lines[yOffset + startRow + i].AddColumn(...)`

  `grep -n "lines\[" Program.cs` で `DrawColumnToLines` 関数の範囲内(現在のおおよその行番号: `DrawColumnToLines` 開始行から `DrawPreviewToLines` 開始行の直前まで)に出現する箇所をすべて洗い出し、漏れなく置換すること。`startRow` 自体の値(`startRow = 1`)は変更しない。加算対象は配列インデックスのみ。

- [x] 1.4 `int visibleHeight = frameHeight - 3;` の行はそのまま変更しない(コメントだけ補強する)。

  ```csharp
  int visibleHeight = frameHeight - 3;  // frameHeight は「このペインに割り当てられた矩形の高さ」(画面全体の高さではない)。
                                         // ヘッダ1行+フルパス行+ステータス行の3行分をこの矩形の高さから引く。
  ```

  **注記**: フルパス行・ステータス行はこの関数のペイン矩形の外(画面最下部2行、共有フッター)に描画されるにもかかわらず、ここで `frameHeight` から引いているのは、単一ペイン時に「ペイン矩形の高さ = 画面全体の高さ - 2」を渡す設計だからである(本計画 Task 3 で `DrawPaneToLines(..., height - 2, ...)` として呼ぶ)。ヘッダ1行分は矩形内で消費するため、実質「渡された高さから ヘッダ1 + (共有フッター2行相当の予約) を引く」という既存の計算がそのまま矩形高さでも正しく機能する。これは Phase 1 以前から存在する既存ロジックであり、この Phase では変更しない。

- [x] 1.5 同様に `DrawPreviewToLines` のシグネチャ末尾にも `int yOffset` を追加する。

  ```csharp
  static void DrawPreviewToLines(Line[] lines, PreviewContent content, int startX, int previewWidth, int frameHeight, int yOffset)
  ```

- [x] 1.6 `DrawPreviewToLines` 本文中の `lines[0]`、`lines[1]`、`lines[2]`、`lines[3]`、`lines[bodyStart]`、`lines[row]`、`lines[i]`(body/pixel ループ内)への書き込みをすべて `yOffset` 加算に変更する。

  具体的な置換対象(現在のコード断片、`yOffset` 加算後の形を併記):

  ```csharp
  // Row 0: type label
  lines[yOffset + 0].AddColumn(headerLabel, headerW, previewWidth);

  if (visibleHeight < 1) return;

  // Row 1: info line
  if (!string.IsNullOrEmpty(content.InfoLine))
  {
      string info = CharacterWidth.SmartTruncate(content.InfoLine, previewWidth - 1);
      int infoW = CharacterWidth.GetStringWidth(info);
      string infoColored = AnsiColors.Colorize(info, AnsiColors.Yellow);
      lines[yOffset + 1].AddColumn(infoColored, infoW, previewWidth);
  }
  else
      lines[yOffset + 1].AddColumn("", 0, previewWidth);

  // Row 2: modified date
  if (!string.IsNullOrEmpty(content.Modified))
  {
      string mod = CharacterWidth.SmartTruncate(content.Modified, previewWidth - 1);
      lines[yOffset + 2].AddColumn(mod, CharacterWidth.GetStringWidth(mod), previewWidth);
  }
  else
      lines[yOffset + 2].AddColumn("", 0, previewWidth);

  // Row 3: divider
  if (visibleHeight >= 3)
  {
      string divider = new string('─', previewWidth - 1);
      lines[yOffset + 3].AddColumn(divider, CharacterWidth.GetStringWidth(divider), previewWidth);
  }

  // Row 4 (optional): extension mismatch warning
  int bodyStart = 4;
  if (!string.IsNullOrEmpty(content.ExtMismatch) && visibleHeight >= bodyStart)
  {
      string warn = CharacterWidth.SmartTruncate(content.ExtMismatch, previewWidth - 1);
      string warnColored = AnsiColors.Colorize(warn, AnsiColors.Yellow);
      lines[yOffset + bodyStart].AddColumn(warnColored, CharacterWidth.GetStringWidth(warn), previewWidth);
      bodyStart = 5;
  }

  // Body lines
  if (content.PixelLines != null)
  {
      for (int i = 0; i < visibleHeight - bodyStart + 1 && i < content.PixelLines.Length; i++)
      {
          int row = bodyStart + i;
          if (row >= visibleHeight + 1) break;
          lines[yOffset + row].AddColumn(content.PixelLines[i], content.PixelWidth, previewWidth);
      }
      int lastPixelRow = bodyStart + Math.Min(content.PixelLines.Length, visibleHeight - bodyStart + 1);
      for (int i = lastPixelRow; i < visibleHeight + 1; i++)
          lines[yOffset + i].AddColumn("", 0, previewWidth);
  }
  else
  {
      for (int i = 0; i < visibleHeight - bodyStart + 1 && i < content.BodyLines.Length; i++)
      {
          int row = bodyStart + i;
          if (row >= visibleHeight + 1) break;
          string bodyLine = CharacterWidth.SmartTruncate(content.BodyLines[i], previewWidth - 1);
          int bodyW = CharacterWidth.GetStringWidth(bodyLine);
          lines[yOffset + row].AddColumn(bodyLine, bodyW, previewWidth);
      }
      int lastBodyRow = bodyStart + Math.Min(content.BodyLines.Length, visibleHeight - bodyStart + 1);
      for (int i = lastBodyRow; i < visibleHeight + 1; i++)
          lines[yOffset + i].AddColumn("", 0, previewWidth);
  }
  ```

  **罠の再確認**: この関数もローカル変数 `row`/`i` 自体は変更しない。`yOffset` は「配列アクセスの瞬間にだけ足す」ものであり、ループの範囲判定(`if (row >= visibleHeight + 1) break;` 等)は矩形内の相対座標のまま計算する。範囲判定にまで `yOffset` を混ぜると二重オフセットになり壊れるため、**加算するのは `lines[...]` の添字だけ**という原則をこの関数でも徹底する。

- [x] 1.7 この時点では呼び出し元(`BuildFrame`)をまだ直していないため、ビルドは失敗する。これは想定通り(Task 2 で呼び出し元を直す)。ここではコンパイルエラーにならないことの確認は行わず、次のタスクへ進む。

- [x] 1.8 `git add Program.cs && git commit -m "refactor: add yOffset parameter to DrawColumnToLines and DrawPreviewToLines"` は行わない。(確認済み: 指示通り単独コミットせず、Task 2 末尾でまとめてコミット済み)ビルドが通らない中間状態でコミットすると `git bisect` 等で壊れた状態が残るため、Task 2 の呼び出し元修正まで完了してからコミットする(Task 2 の末尾でまとめてコミットする)。

---

### Task 2: `DrawPaneToLines` の新設と `BuildFrame` の3層分解

**Files:**
- Modify: `Program.cs`(`BuildFrame`、新規 `DrawPaneToLines`、新規空フック `DrawDialogOverlay`)

#### ステップ

- [x] 2.1 `DrawColumnToLines` の直前(関数定義の直前)に `DrawPaneToLines` を新設する。

  ```csharp
  static void DrawPaneToLines(Line[] lines, Pane pane, int yOffset, int height, bool focused, int width)
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
          DrawColumnToLines(lines, pane.Columns[i], displayX, i == pane.ActiveColumn, i < pane.ActiveColumn, width, height, isFirstVisible, yOffset);
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
              DrawPreviewToLines(lines, content, drawnColumnsWidth, previewWidth, height, yOffset);
      }

      // focused はこの Phase では常に true(減光パレット導入は Phase 5)。
      // 引数として受け取るだけで、この Phase の DrawColumnToLines/DrawPreviewToLines には伝播しない
      // (両関数のシグネチャに focused を追加するのは Phase 5 の責務)。
      _ = focused;
  }
  ```

  **設計上の注記(なぜ `height` を `DrawColumnToLines`/`DrawPreviewToLines` に「そのまま」渡してよいか)**: `DrawPaneToLines` はすでに「そのペインに割り当てられた矩形の高さ」を `height` として受け取る契約にしている(呼び出し元の `BuildFrame` が単一ペイン時は `height - 2` を渡す)。したがって `DrawPaneToLines` の内部で `DrawColumnToLines(..., height, ...)` とそのまま渡せば、Task 1 で確認した「`frameHeight` = そのペインの高さ」という前提が自動的に満たされる。`DrawPaneToLines` 自身は高さの意味変換を行わない中継役に徹する。

  **`focused` 引数について**: この Phase では `DrawColumnToLines`/`DrawPreviewToLines` に減光パレット分岐を実装しない(spec: 「減光パレット・分割はこの Phase ではやらない(Phase 5)」)。`DrawPaneToLines` のシグネチャに `focused` を含めるのは、Phase 5 が `DrawPaneToLines` の呼び出し規約(引数の並び)を変更せずに中身だけ拡張できるようにするための先行導入であり、spec の指示通り「`focused` 引数だけは今回導入し、常に true を渡す」を満たす。`_ = focused;` は未使用引数警告を抑止するための明示的な破棄であり、Phase 5 で実際に使われ始めたら削除する。

- [x] 2.2 `BuildFrame` を書き換える。ペイン矩形描画・共有フッター・オーバーレイ空フックの3層に分解する。**⚠️ 実装時の乖離あり — 下の「実装後の訂正」参照。**

  ```csharp
  static string[] BuildFrame(int width, int height)
  {
      string[] frame = new string[height];

      // Build frame using segment-based approach
      Line[] lines = new Line[height];
      for (int i = 0; i < height; i++)
          lines[i] = new Line();

      Pane pane = FocusedPane;

      // --- 層1: ペイン矩形描画 ---
      // この Phase では単一ペイン固定(Panes.Count == 1, SplitActive == false)。
      // yOffset = 0, height = height - 2(フッター2行を除いた残り全部)、focused = true固定。
      DrawPaneToLines(lines, pane, 0, height - 2, true, width);

      // Render each line to string and pad to window width
      for (int i = 0; i < height - 2; i++)
      {
          frame[i] = lines[i].Render(width);
      }

      // --- 層2: 共有フッター ---
      // Full path line (above status)
      string fullPath = GetCurrentFullPath(pane);
      fullPath = CharacterWidth.SmartTruncate(fullPath, width);
      frame[height - 2] = CharacterWidth.PadToWidth(fullPath, width);

      // Override status with preview-hidden hint only when terminal width is the limiting factor
      if (pane.Preview.IsVisible && SelectedItemIsFileOrDrive(pane) && Console.WindowWidth < ColumnWidth * 2)
      {
          _lastErrorMessage = "[preview hidden: narrow terminal]";
      }

      // Status line
      if (pane.Leap.Active)
      {
          string leapBar = SearchHelper.BuildLeapStatusBar(pane.Leap, pane.Columns, pane.ActiveColumn, width);
          frame[height - 1] = leapBar;
      }
      else if (pane.Search.Active)
      {
          List<int> matchSnapshot;
          bool done;
          lock (_searchLock)
          {
              matchSnapshot = new List<int>(pane.Search.Matches);
              done = pane.Search.SearchDone;
          }
          string searchBar = SearchHelper.BuildSearchStatusBar(pane.Search, matchSnapshot, done, width);
          frame[height - 1] = searchBar; // already padded to width
      }
      else if (_lastErrorMessage != null)
      {
          string status = _lastErrorMessage;
          _lastErrorMessage = null;  // Clear error after displaying
          status = CharacterWidth.SmartTruncate(status, width);
          frame[height - 1] = CharacterWidth.PadToWidth(status, width);
      }
      else
      {
          string previewHint = pane.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
          string status;
          if (IsWindows())
              status = $"Esc=Quit | Ctrl+Enter=Open | Shift+Enter/\\=Menu | {previewHint}";
          else
              status = $"Esc=Quit | Ctrl+Enter=Open File | {previewHint}";
          status = CharacterWidth.SmartTruncate(status, width);
          frame[height - 1] = CharacterWidth.PadToWidth(status, width);
      }

      // --- 層3: オーバーレイ(空フック。Phase 3 で DialogState 導入後に実装する) ---
      DrawDialogOverlay(frame, width, height);

      return frame;
  }

  static void DrawDialogOverlay(string[] frame, int width, int height)
  {
      // Phase 3 まで DialogState は存在しないため、この Phase では常に no-op。
      // Phase 3 で「State.Dialog != null の場合、frame の該当範囲へ中央ボックスを上書きする」
      // 処理をここに実装する(spec「ダイアログ設計」節)。
  }
  ```

  **実装後の訂正(未解決事項 §1 参照)**: 上のコード例は `DrawPaneToLines(lines, pane, 0, height - 2, true, width);` として `height - 2` を渡しているが、これは誤り。`DrawPaneToLines` は `height` 引数をそのまま `DrawColumnToLines`/`DrawPreviewToLines` の `frameHeight` に転送するため、`height - 2` を渡すと `visibleHeight` が `frameHeight - 3` の計算で本来より2小さくなり(`height-5` になる)、単一ペインの表示行数が2行減って画面出力が変更前と一致しなくなる(バイト同一性の破壊)。実装では `DrawPaneToLines(lines, pane, 0, height, true, width);`(`height` をそのまま渡す)に修正し、`dotnet test` 全緑を維持したまま画面出力の不変性を保った。詳細はコミット `2f5fda9` およびファイル末尾の「未解決事項」を参照。

  **`GetCurrentFullPath`/`SelectedItemIsFileOrDrive` について**: Phase 1 のリファクタで両者は `GetCurrentFullPath(Pane pane)`/`SelectedItemIsFileOrDrive(Pane pane)` になっている前提(spec の変換規則: `State.ActiveColumn` を参照する static メソッドは先頭引数 `Pane pane` を取る)。実際のシグネチャがこれと異なる場合、実装セッションは呼び出し側を実シグネチャに機械的に合わせてよい(コンパイルが通る書き方の選択)。ただし関数が `Pane` を受け取らず依然として static グローバルを直接参照している場合(= Phase 1 が未完了)は「未解決事項」に記録し停止する。

- [x] 2.3 `Program.cs` 全体で `DrawColumnToLines`/`DrawPreviewToLines` を直接呼んでいる箇所が `DrawPaneToLines` 以外に残っていないか確認する。

  ```
  grep -n "DrawColumnToLines\|DrawPreviewToLines" Program.cs
  ```

  期待: 両関数の定義行と、`DrawPaneToLines` 内の呼び出し1箇所ずつのみ。他に呼び出しが残っている場合はすべて `yOffset` 引数を追加するか `DrawPaneToLines` 経由に統一する。

- [x] 2.4 `dotnet build Zurari.csproj` を実行する。**期待結果**: exit code 0。(確認済み)Task 1 で追加した `yOffset` 引数まわりのビルドエラーがここですべて解消されているはず。解消していない場合は該当箇所を修正してから次へ進む。

- [x] 2.5 `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行する。**期待結果**: `Passed!`(全緑)。(確認済み: 64/64)既存テストは `DrawColumnToLines`/`DrawPreviewToLines`/`BuildFrame` を直接呼んでいないはずなので(`NavigationTests` 等はヘルパ関数単体をテストしている)、影響がないことを確認する。

- [x] 2.6 `git add Program.cs && git commit -m "refactor: split BuildFrame into pane-rect / footer / overlay-hook layers via DrawPaneToLines"` を実行する。(確認済み: コミット `c9743ee`。ただし 2.2 の乖離により直後にコミット `2f5fda9` で `height-2`→`height` の修正が必要になった。)

---

### Task 3: 見た目不変の検証 — 純関数化できる部分のユニットテスト

`BuildFrame` 自体は `Console.WindowWidth`/`Console.WindowHeight`/`State`/`_lastErrorMessage` などの外部状態に依存するため丸ごとユニットテスト化はできない。だが、この Phase で導入した「フッター合成」「`yOffset` 変換」のロジックは、外部状態から独立した形で個別に検証できる。

**Files:**
- Test: `Zurari.Tests/ViewportRenderingTests.cs`(新規作成)

#### ステップ

- [x] 3.1 `yOffset` 変換の性質(`yOffset = 0` のとき従来の絶対インデックスと完全に一致すること)を検証する軽量テストを書く。`DrawColumnToLines`/`DrawPreviewToLines` は `internal` な `static class Program` のメンバであり、`InternalsVisibleTo("Zurari.Tests")` により `Zurari.Tests` から直接呼び出せる。これを利用し、「同一の `Column`/`PreviewContent` を `yOffset=0` で描画した結果」と「`yOffset=N` で描画し、書き込まれた行を `N` だけ逆シフトした結果」が一致することを確認する(= オフセット加算がずれなく機能していることの直接証拠)。

  ```csharp
  using System.Collections.Generic;
  using Xunit;

  public class ViewportRenderingTests
  {
      static Column MakeColumn(int entryCount, int selected = 0)
      {
          var c = new Column("C:\\test");
          for (int i = 0; i < entryCount; i++)
              c.Entries.Add($"file{i:D3}.txt");
          c.Selected = selected;
          return c;
      }

      static Line[] NewLines(int height)
      {
          var lines = new Line[height];
          for (int i = 0; i < height; i++)
              lines[i] = new Line();
          return lines;
      }

      [Fact]
      public void DrawColumnToLines_YOffsetZero_MatchesAbsoluteBaseline()
      {
          // frameHeight=10 のペイン矩形、entries 5件、非アクティブカラムという単純ケース。
          const int frameHeight = 10;
          const int columnWidth = 32;

          Column baseline = MakeColumn(5, selected: 2);
          Line[] baselineLines = NewLines(frameHeight);
          Program.DrawColumnToLines(baselineLines, baseline, 0, active: false, isLeft: false,
              frameWidth: columnWidth, frameHeight: frameHeight, isFirstVisible: true, yOffset: 0);

          Column shifted = MakeColumn(5, selected: 2);
          Line[] shiftedLines = NewLines(frameHeight);
          Program.DrawColumnToLines(shiftedLines, shifted, 0, active: false, isLeft: false,
              frameWidth: columnWidth, frameHeight: frameHeight, isFirstVisible: true, yOffset: 0);

          for (int i = 0; i < frameHeight; i++)
              Assert.Equal(baselineLines[i].Render(columnWidth), shiftedLines[i].Render(columnWidth));
      }

      [Fact]
      public void DrawColumnToLines_NonZeroYOffset_ShiftsWrittenRowsBySameAmount()
      {
          // yOffset=3 で描画した結果を3行分逆シフトすると、yOffset=0 の結果と一致するはず。
          const int frameHeight = 8;   // このペインの矩形高さ(画面全体ではない)
          const int columnWidth = 32;
          const int yOffset = 3;
          const int totalHeight = frameHeight + yOffset;

          Column a = MakeColumn(4, selected: 1);
          Line[] zeroOffsetLines = NewLines(frameHeight);
          Program.DrawColumnToLines(zeroOffsetLines, a, 0, active: true, isLeft: false,
              frameWidth: columnWidth, frameHeight: frameHeight, isFirstVisible: true, yOffset: 0);

          Column b = MakeColumn(4, selected: 1);
          Line[] shiftedLines = NewLines(totalHeight);
          Program.DrawColumnToLines(shiftedLines, b, 0, active: true, isLeft: false,
              frameWidth: columnWidth, frameHeight: frameHeight, isFirstVisible: true, yOffset: yOffset);

          for (int i = 0; i < frameHeight; i++)
              Assert.Equal(zeroOffsetLines[i].Render(columnWidth), shiftedLines[yOffset + i].Render(columnWidth));

          // yOffset より上の行は一切書き込まれていない(空のまま)ことも確認する。
          for (int i = 0; i < yOffset; i++)
              Assert.Equal(new string(' ', columnWidth), shiftedLines[i].Render(columnWidth));
      }
  }
  ```

  **注記**: `Program.DrawColumnToLines` を直接呼ぶには `internal` アクセスで足りるため `public`/`internal` 修飾子の変更は不要(top-level の `static class Program` は既定で `internal`、`Zurari.Tests` は `InternalsVisibleTo` 経由でアクセス可能)。もし `DrawColumnToLines` が `private` 修飾子を明示的に持っていてテストから見えない場合のみ、`private` を削除して既定の `internal` に変える(この変更自体はアクセシビリティの拡大のみでロジックに影響しないため、判断保留せず実施してよい)。

- [x] 3.2 フッター合成(パス行+ステータス行)のうち、外部状態(`Console.WindowWidth` 等)に依存しない部分 — 「与えられた `fullPath` 文字列と `width` から最終的なパス行文字列を作る」ロジック — を検証する。これは `BuildFrame` 内でインライン化されている次の2行の組み合わせと同じ計算である。

  ```csharp
  fullPath = CharacterWidth.SmartTruncate(fullPath, width);
  frame[height - 2] = CharacterWidth.PadToWidth(fullPath, width);
  ```

  これは `CharacterWidth` の既存ロジックそのものであり、`CharacterWidth` 自体のユニットテストは対象外(既存の他テストファイルでカバーされている前提)。ここでは「この2行の組み合わせが `width` ちょうどの表示幅になる」という合成結果だけを軽く確認する。

  ```csharp
  public class FooterCompositionTests
  {
      [Theory]
      [InlineData("C:\\Users\\test\\short.txt", 40)]
      [InlineData("C:\\very\\long\\path\\that\\exceeds\\the\\available\\width\\file.txt", 40)]
      [InlineData("", 20)]
      public void FullPathLine_AlwaysRendersExactlyToWidth(string rawPath, int width)
      {
          string truncated = CharacterWidth.SmartTruncate(rawPath, width);
          string padded = CharacterWidth.PadToWidth(truncated, width);
          Assert.Equal(width, CharacterWidth.GetStringWidth(padded));
      }
  }
  ```

- [x] 3.3 `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行する。**期待結果**: `Passed!`(全緑)。(確認済み: 69/69 = 64既存 + 2 Fact + 3 Theory)新規4テスト(`ViewportRenderingTests` 2件 + `FooterCompositionTests` 3件、Theory のためカウントは実行時に確認)がすべて通ること。

- [x] 3.4 `git add Zurari.Tests/ViewportRenderingTests.cs && git commit -m "test: verify yOffset row-shift invariant and footer composition for viewport rendering"` を実行する。(確認済み: コミット `052dc7b`。`Program.cs` の `internal` 修飾子追加も同コミットに含めた — テストから `DrawColumnToLines`/`DrawPreviewToLines`/`State` を呼ぶために必要だったため。)

---

## 手動確認手順(Manual Verification)

`BuildFrame` の外部状態依存部分(`Console.WindowWidth`/`Console.WindowHeight`、実際のディレクトリ内容、プレビュー描画、Leap/Search のライブな状態遷移)は自動テストで再現しきれないため、実機での目視確認が必要。ターミナルは 30行×100列程度を推奨。

### M1: 起動直後の基本レイアウト
1. `dotnet run --project Zurari.csproj` でアプリを起動する。
2. **期待画面**: Phase 1 以前(この Phase の変更前)と全く同じ、Miller カラム一覧・パス行・ステータス行のレイアウトが表示される。カラムのヘッダ・エントリ一覧・選択行のハイライトに崩れがない。

### M2: 数ディレクトリ移動
1. `↓`/`↑` でカーソルを動かし、`Enter` で2〜3階層ディレクトリを移動する。
2. **期待画面**: 新しいカラムが右側に追加され、既存カラムのハイライト位置が正しく更新される。スクロールが必要な深い一覧でも、選択行が画面内に収まるよう自動スクロールする(Task 1.4 の `visibleHeight` 計算がそのまま機能していることの確認)。
3. `Backspace` で親ディレクトリへ戻る。
4. **期待画面**: 右側のカラムが破棄され、レイアウトが正しく縮む。

### M3: プレビュー on/off
1. `Shift+V` でプレビューを表示する。
2. **期待画面**: ファイルにカーソルを合わせた際、右側にプレビューペイン(タイプラベル・情報行・更新日時・区切り線・本文)が表示される。表示位置・折り返しが Phase 1 以前と同一。
3. 端末幅を `ColumnWidth * 2` 未満まで縮める。
4. **期待画面**: ステータス行に `[preview hidden: narrow terminal]` が表示され、プレビューが消える。
5. `Shift+V` でプレビューを非表示に戻す。
6. **期待画面**: ステータス行のヒントが `Shift+V=Preview[off]` に戻る。

### M4: 検索
1. `/` を押して検索モードに入り、ファイル名の一部を入力する。
2. **期待画面**: ステータス行が検索バーに切り替わり、マッチしたエントリがハイライトされる(現在マッチ=緑背景、他マッチ=緑前景)。カラム内の描画位置がずれていない。
3. `Esc` で検索を終了する。
4. **期待画面**: ステータス行が通常のヒント表示に戻る。

### M5: Leap
1. `F3` を押して Leap モードに入り、文字を数文字入力する。
2. **期待画面**: ステータス行が Leap バーに切り替わり、マッチしないエントリが非表示になった状態でカラムが再描画される(スクロール位置もマッチ集合に対して正しく追従する)。
3. `Esc` で Leap を終了する。
4. **期待画面**: 全エントリが元通り表示され、カーソル位置が Leap 開始前の状態に整合する。

### M6: 総合(見た目不変の最終確認)
1. M1〜M5 を一通り行った後、改めてアプリを再起動し、この Phase の変更前(`git stash` または変更前コミットに一時的に切り替えて同一操作を行う、もしくは目視記憶で比較)と同じ操作列を実行する。
2. **期待結果**: どの画面でも、この Phase による変更が視覚的に一切検知できない(色・位置・スクロール挙動すべて同一)。差異があれば `yOffset` 加算箇所の見落とし(Task 1.3/1.6)を疑い、`grep -n "lines\[" Program.cs` で該当関数内の書き込み箇所を再点検する。

---

## 完了条件チェックリスト

- [x] `DrawColumnToLines`/`DrawPreviewToLines` の書き込み先インデックスすべてに `yOffset` が加算されている(`grep -n "lines\[" Program.cs` で当該2関数の範囲を目視確認)。
- [x] `DrawPaneToLines(Line[] lines, Pane pane, int yOffset, int height, bool focused, int width)` が新設され、`BuildFrame` から `yOffset=0, height=height, focused=true` で呼ばれている(**`height-2` ではなく `height`** — 未解決事項 §1 参照)。
- [x] `BuildFrame` が「ペイン矩形描画 → 共有フッター → オーバーレイ(空フック `DrawDialogOverlay`)」の3層構造になっている。
- [x] `DrawColumnToLines`/`DrawPreviewToLines` の直接呼び出しが `DrawPaneToLines` 経由の1箇所ずつに統一されている(他に残っていない)。
- [x] `dotnet build Zurari.csproj` が exit 0 で成功する。
- [x] `dotnet test Zurari.Tests/Zurari.Tests.csproj` が全緑(`Passed!`)である(69/69: 既存64 + 新規 `ViewportRenderingTests` 2 Fact + `FooterCompositionTests` 3 Theory)。
- [x] 単一ペイン・ダイアログなしの状態で、手動確認手順(M1〜M6)により Phase 1 以前と画面出力が視覚的に同一であることを確認した。(ユーザーが `dotnet run --project Zurari.csproj` で実機確認済み)
- [x] `focused` 引数は `DrawPaneToLines` に存在し、常に `true` が渡されている(減光パレット自体は未実装、Phase 5 に持ち越し)。

## 未解決事項

1. **Task 2.2 のコード例が誤っていた(`height - 2` → `height` に訂正)。** 計画書の Task 2.2 に記載された `DrawPaneToLines(lines, pane, 0, height - 2, true, width);` をそのまま実装すると、`DrawPaneToLines` がその `height` 引数を無変換で `DrawColumnToLines`/`DrawPreviewToLines` の `frameHeight` に転送するため、`visibleHeight = frameHeight - 3` の計算が `height - 5` になり(元は `height - 3`)、単一ペインの表示行数が2行減って画面最下部に空行2行が生じる。これは本計画の中心的な受け入れ条件「単一ペイン・ダイアログなしの状態で、変更前と画面出力が完全に同一であること」に直接反する。
   - 発覚経緯: サブエージェントによる code-quality review(2段階レビューの2段目)がこの回帰を検出。計画書の記述を機械的にコピーした結果であり、実装セッション側の読み替え判断ミスではない。
   - 対応: `DrawPaneToLines(lines, pane, 0, height, true, width);`(`height` をそのまま渡す)に修正するコミット `2f5fda9` を追加。修正後、`dotnet build`/`dotnet test`(69/69)ともに green を維持。
   - 今後の Phase(特に Phase 5 分割ペイン)への影響: 将来、実際に画面を複数ペインに分割して各ペインの高さがフッター2行分を除いた画面全体より小さくなる場合、`DrawColumnToLines`/`DrawPreviewToLines` 内の `visibleHeight = frameHeight - 3` という「3行予約」計算自体を見直す必要がある(現状はヘッダ1行+共有フッター2行を暗黙に同じ `frameHeight` 予算から引く設計だが、共有フッターは既にペイン矩形の外にあるため、この「3」という数値の意味を Phase 5 で再設計しないと同様の行数ズレが再発する)。Phase 5 の計画作成時にこの点を明記すること。
2. ~~手動確認手順(M1〜M6)は実行セッション内では検証できていない。~~ → ユーザーが `dotnet run --project Zurari.csproj` で M1〜M6 を実機確認し、Phase 1 以前と画面出力が視覚的に同一であることを確認済み。Phase 2 は完了条件チェックリスト全項目クリア。
