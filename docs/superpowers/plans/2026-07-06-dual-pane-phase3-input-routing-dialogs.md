# Phase 3: 入力ルーティング一般化 + ダイアログ3部品 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** メインループの入力分岐を「ダイアログ > フォーカスペインの Leap/Search > フォーカスペインの通常キー」の1系統に統合し、確認/1行テキスト入力/リスト選択の3種のオーバーレイダイアログを実装する。
**Architecture:** `DialogState` 抽象クラスとその3派生(`ConfirmDialogState`/`TextInputDialogState`/`ListSelectDialogState`)を新設し、`ScreenState.Dialog` に保持する。開く API(`ShowConfirm`/`ShowTextInput`/`ShowListSelect`)は状態をセットして即座に返る非モーダル関数とし、結果は `Action<T>` コールバックで通知する。入力は `HandleDialogKeyAsync` が一手に引き受け、`Main` のメインループはこれを最優先に呼ぶよう分岐を組み替える。描画は `BuildFrame` が3層合成(ペイン矩形描画→共有フッター)を終えて `string[] frame` を完成させた直後に `ComposeDialogIntoFrame(string[] frame, DialogState dialog, int width, int height)` を呼び、中央固定・自動サイズのボックスを重ね書きする(`Line[]` 完成前ではなく `frame[]` 完成後に書き込む理由は本文「実装に入る前の設計メモ」参照)。レイアウト計算(`CalcDialogRect`)・テキストバッファ編集・リスト絞り込みは純関数/独立ヘルパとして切り出し、ユニットテストで先に固める。
**Tech Stack:** C# / .NET 8, xUnit(Zurari.Tests), 自前 TUI(外部 TUI フレームワーク禁止)

---

## 前提条件(開始前に検証)

- [ ] Phase 1・Phase 2 が完了していること。次のコマンドと目視確認で検証する。
  - `dotnet build Zurari.csproj` → exit 0。
  - `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!` 全緑。
  - `grep -n "sealed class Pane\|List<Pane> Panes\|int FocusedPane\|bool SplitActive\|DialogState? Dialog" Program.cs` を実行し、`sealed class Pane` と `ScreenState` 内の `Panes`/`FocusedPane`/`SplitActive`/`Dialog`(宣言のみでよい)がヒットすることを確認する。
  - `grep -n "static Pane FocusedPane\|static.*HandleSpecialKeyAsync(Pane\|static.*HandleSearchKeyAsync(Pane\|static.*HandleLeapKeyAsync(Pane" Program.cs` を実行し、ナビゲーション関数群が `Pane pane` を先頭引数に取る形にリファクタリング済みであることを確認する。
  - `grep -n "DrawPaneToLines\|3層\|yOffset" Program.cs` 相当の確認、または `BuildFrame` を目視し、「ペイン矩形描画 → 共有フッター → オーバーレイ」の3層構成に分解済みであること、単一ペイン・ダイアログなし状態で Phase 2 以前と画面出力が同一であることを確認する。
  - 上記のいずれかが欠けている場合、本計画のコード断片(`pane` 引数、`FocusedPane`、`DrawPaneToLines` 等の前提)はそのまま適用できない。実行セッションは自己判断で `Pane` 抽出やビューポート分割を即興実装せず、「未解決事項」に「Phase 1/2 未完了のため本計画を字面通り適用できない」旨を記録してユーザーに Phase 1/2 の計画実行を先に依頼するよう報告し、停止すること。

この計画と spec に書かれていない判断が必要になったら、自分で決めずに「未解決事項」に記録してそのステップを保留し、次の独立したタスクに進むか停止すること。

---

## 実装に入る前の設計メモ(このセッションが確認した既存コードとの対応)

- `Line`(`Program.cs:199`)は現状 `AddColumn`(列の追記)と `Render(int targetWidth)` のみを持ち、任意の x 位置への上書き機能がない。ガイドは `DrawDialogToLines(Line[] lines, ...)` というシグネチャを指定しているが、`BuildFrame` の実際の構造では `Line[]` はパス行・ステータス行(`height - 2`/`height - 1` 行目)の書き込みより**前**の段階(`height - 2` 行分のみ)で `frame[]` に変換されてしまい、その後パス行・ステータス行は `frame[]` に直接文字列として上書きされる(`Program.cs:3457`-`3508` 付近)。ダイアログの中央ボックスはこの2行にも重なってよいオーバーレイのため、`Line[]` の段階で書き込んでも手遅れになるケースがある。したがって本計画では `Line` の拡張ではなく、`frame[]` が完成した最後の瞬間に働く `ComposeDialogIntoFrame(string[] frame, ...)` を新設する(Task 8 参照)。これは spec の「オーバーレイは `Line[]` 上へ書く」という記述の実装上の翻訳であり、`Line[]` という中間データ構造そのものへのこだわりではなく「3層合成の最後に重ね書きする」という設計意図を維持することを優先した判断である。
- `HandleSearchKeyAsync`(`Program.cs:3159`)の文字受け取り判定は `if (!char.IsControl(key.KeyChar)) { s.Query += key.KeyChar; ... }` であり、`Backspace`/`Escape`/`Enter`/矢印/Ctrl+R などの特殊キーを先に個別分岐で捌いた後の残りを「制御文字でなければ挿入」で拾う構造になっている。IME確定文字・サロゲートペアは `Console.ReadKey` が1回のキーイベントとして `KeyChar` に渡す高位/下位サロゲートそれぞれを個別の `ConsoleKeyInfo` として送ってくる場合があるが、`char.IsControl` はサロゲート文字を制御文字と判定しないため、そのまま `Buffer` へ追記すれば .NET の `string`(UTF-16)がサロゲートペアを保持し、`Console.Write` 時に正しく結合されて表示される。本計画のテキスト入力ダイアログはこの経路をそのまま流用し、新しい入力処理を書かない。
- `CharacterWidth.GetStringWidth`/`SmartTruncate`/`PadToWidth`(`Program.cs:265`-`413`)をレイアウト計算・カーソル位置計算・切り詰めにそのまま使う。
- `SearchHelper.MatchesSearchQuery(entry, query, regexMode, migemo)`(`Program.cs:590`)をリスト絞り込みにそのまま使う(`regexMode: false` 固定、`_migemo` 共有インスタンスを渡す)。
- 共有 static(`_migemo`, `_lastErrorMessage`, `_searchLock`, カーソルメモリ)は本 Phase では触らない。
- `ScreenState.Dialog` は Phase 1 で宣言済み(`DialogState? Dialog;`)という前提だが、`DialogState` 型自体は本 Phase で新設する。Phase 1 の成果物に `DialogState` の前方宣言がない場合(コンパイルエラーになる場合)は、本 Phase の Task 1 で `DialogState` を先に定義してから `ScreenState.Dialog` フィールドを有効化する順序で進める。

---

### Task 1: DialogState 階層の新設

**Files:**
- Modify: `Program.cs`

- [ ] 1.1 `Program.cs` に `DialogState` 抽象クラスと3派生クラスを追加する。挿入位置: `sealed class ScreenState`(`Program.cs:446` 付近、Phase 1 リファクタ後の位置)の直後。

  ```csharp
  abstract class DialogState
  {
      public string Title = "";
  }

  sealed class ConfirmDialogState : DialogState
  {
      public string Message = "";
      public Action<bool?> OnResult = _ => { };   // true=Yes, false=No, null=Esc
  }

  sealed class TextInputDialogState : DialogState
  {
      public string Buffer = "";
      public int CursorPos;
      public Action<string?> OnResult = _ => { }; // null=キャンセル
  }

  sealed class ListSelectDialogState : DialogState
  {
      public List<string> AllItems = new();
      public List<string> FilteredItems = new();
      public string FilterQuery = "";
      public int Selected;
      public Action<string?> OnResult = _ => { }; // 確定項目の文字列 or null
  }
  ```

- [ ] 1.2 `ScreenState` に `DialogState? Dialog;` フィールドが存在することを確認する。存在しない場合(Phase 1 の成果物に含まれていなかった場合)はここで追加する。

  ```csharp
  sealed class ScreenState
  {
      // ...(Phase 1/2 で確立済みの既存フィールド)...
      public DialogState? Dialog;
  }
  ```

- [ ] 1.3 `dotnet build Zurari.csproj` を実行する。**期待結果**: exit 0(新規クラス追加のみで既存コードへの参照はまだないため、既存箇所への影響はない)。
- [ ] 1.4 コミットする。
  ```
  git add Program.cs
  git commit -m "feat: add DialogState hierarchy (Confirm/TextInput/ListSelect)"
  ```

---

### Task 2: ダイアログレイアウト計算(TDD) — `CalcDialogRect`

ダイアログの幅・高さ・中央配置座標を入力サイズから算出する純関数を先にテストで固める。この関数は `DialogState` にもコンソールにも依存しない。

**Files:**
- Create: `Zurari.Tests/DialogLayoutTests.cs`
- Modify: `Program.cs`

- [ ] 2.1 `Zurari.Tests/DialogLayoutTests.cs` を新規作成し、以下のテストを書く。

  ```csharp
  using Xunit;

  public class DialogLayoutTests
  {
      // レイアウト: 内容(幅・高さ)と画面サイズから中央配置の矩形を算出する。
      // 戻り値は (X, Y, Width, Height)。Width/Height は枠線2文字分を含む外形サイズ。

      [Fact]
      public void CalcDialogRect_CentersSmallContentInLargeScreen()
      {
          var rect = DialogLayout.CalcDialogRect(contentWidth: 20, contentHeight: 3, screenW: 100, screenH: 40);

          // 外形 = 内容 + 枠線2(左右/上下それぞれ1)
          Assert.Equal(22, rect.Width);
          Assert.Equal(5, rect.Height);
          // 中央配置: X = (100 - 22) / 2 = 39, Y = (40 - 5) / 2 = 17
          Assert.Equal(39, rect.X);
          Assert.Equal(17, rect.Y);
      }

      [Fact]
      public void CalcDialogRect_OddRemainderLeansTopLeft()
      {
          // 画面99x39、外形22x5 のとき余り(99-22=77, 39-5=34)は偶数なので中央ぴったり。
          // 余りが奇数になるケースで切り捨て(小さい方)がX/Yどちらに寄るかを固定する。
          var rect = DialogLayout.CalcDialogRect(contentWidth: 19, contentHeight: 3, screenW: 100, screenH: 40);
          // 外形 = 21x5。余り X: 100-21=79 (奇数) → 79/2=39 (切り捨て)。余り Y: 40-5=35(奇数)→35/2=17。
          Assert.Equal(39, rect.X);
          Assert.Equal(17, rect.Y);
      }

      [Fact]
      public void CalcDialogRect_ClampsWidthToScreenWidthMinusMargin()
      {
          // 内容が画面より大きい場合、外形は画面幅から左右マージン2文字を引いた値でクランプする。
          var rect = DialogLayout.CalcDialogRect(contentWidth: 200, contentHeight: 3, screenW: 40, screenH: 20);

          Assert.Equal(38, rect.Width);   // 40 - 2(マージン)
          Assert.Equal(1, rect.X);        // 中央寄せの結果、左マージン1
      }

      [Fact]
      public void CalcDialogRect_ClampsHeightToScreenHeightMinusMargin()
      {
          var rect = DialogLayout.CalcDialogRect(contentWidth: 10, contentHeight: 100, screenW: 60, screenH: 20);

          Assert.Equal(18, rect.Height);  // 20 - 2(マージン、上下フッター2行を考慮しない単純ケース)
          Assert.Equal(1, rect.Y);
      }

      [Fact]
      public void CalcDialogRect_MinimumSizeIsRespectedEvenForTinyContent()
      {
          // 内容が極小(幅0高さ0)でもタイトル枠が成立する最小外形(幅4×高さ3)を下回らない。
          var rect = DialogLayout.CalcDialogRect(contentWidth: 0, contentHeight: 0, screenW: 100, screenH: 40);

          Assert.True(rect.Width >= 4);
          Assert.True(rect.Height >= 3);
      }

      [Fact]
      public void CalcDialogRect_NeverExceedsScreenBounds()
      {
          var rect = DialogLayout.CalcDialogRect(contentWidth: 500, contentHeight: 500, screenW: 30, screenH: 15);

          Assert.True(rect.X >= 0);
          Assert.True(rect.Y >= 0);
          Assert.True(rect.X + rect.Width <= 30);
          Assert.True(rect.Y + rect.Height <= 15);
      }
  }
  ```

- [ ] 2.2 `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter DialogLayoutTests` を実行し、`DialogLayout` 未定義のコンパイルエラーで失敗することを確認する。

- [ ] 2.3 `Program.cs` に `DialogLayout` 静的クラスと `DialogRect` レコードを追加する。挿入位置: `DialogState` 系クラス(Task 1)の直後。

  ```csharp
  readonly record struct DialogRect(int X, int Y, int Width, int Height);

  static class DialogLayout
  {
      const int MarginEachSide = 1; // 画面端からの最小余白(片側)
      const int MinContentWidth = 2;
      const int MinContentHeight = 1;

      public static DialogRect CalcDialogRect(int contentWidth, int contentHeight, int screenW, int screenH)
      {
          int cw = Math.Max(contentWidth, MinContentWidth);
          int ch = Math.Max(contentHeight, MinContentHeight);

          int outerWidth = cw + 2;   // 左右の枠線1文字ずつ
          int outerHeight = ch + 2;  // 上下の枠線1文字ずつ

          int maxWidth = Math.Max(4, screenW - MarginEachSide * 2);
          int maxHeight = Math.Max(3, screenH - MarginEachSide * 2);

          outerWidth = Math.Min(outerWidth, maxWidth);
          outerHeight = Math.Min(outerHeight, maxHeight);

          int x = Math.Max(0, (screenW - outerWidth) / 2);
          int y = Math.Max(0, (screenH - outerHeight) / 2);

          // 画面外にはみ出さないようクランプ(screenW/screenH が極小な場合の保険)
          if (x + outerWidth > screenW) x = Math.Max(0, screenW - outerWidth);
          if (y + outerHeight > screenH) y = Math.Max(0, screenH - outerHeight);

          return new DialogRect(x, y, outerWidth, outerHeight);
      }
  }
  ```

- [ ] 2.4 `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter DialogLayoutTests` を実行し、6件すべて Passed になることを確認する。失敗する場合は期待値の算出根拠(枠線2文字・マージン1文字ずつ)を見直し、テストとコードのどちらか矛盾している側を修正すること(テストの期待値自体を安易に書き換えず、まず算出式のコメントと突き合わせる)。
- [ ] 2.5 `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 2.6 コミットする。
  ```
  git add Program.cs Zurari.Tests/DialogLayoutTests.cs
  git commit -m "feat: add DialogLayout.CalcDialogRect pure layout function (TDD)"
  ```

---

### Task 3: テキスト入力バッファ編集(TDD) — カーソル位置管理

`TextInputDialogState.Buffer`/`CursorPos` に対する挿入・削除・移動をカーソル単位(UTF-16 コードユニット単位。サロゲートペアの分割は許容しない)で扱う純粋なヘルパ関数群を先にテストで固める。

**Files:**
- Create: `Zurari.Tests/TextInputBufferTests.cs`
- Modify: `Program.cs`

- [ ] 3.1 `Zurari.Tests/TextInputBufferTests.cs` を新規作成する。

  ```csharp
  using Xunit;

  public class TextInputBufferTests
  {
      [Fact]
      public void InsertChar_AtEnd_AppendsAndAdvancesCursor()
      {
          var (buf, pos) = TextInputBuffer.Insert("abc", 3, 'd');
          Assert.Equal("abcd", buf);
          Assert.Equal(4, pos);
      }

      [Fact]
      public void InsertChar_AtMiddle_SplicesAndAdvancesCursor()
      {
          var (buf, pos) = TextInputBuffer.Insert("ac", 1, 'b');
          Assert.Equal("abc", buf);
          Assert.Equal(2, pos);
      }

      [Fact]
      public void InsertChar_CJK_CountsAsSingleCodeUnitForCursor()
      {
          // '漢' は1 UTF-16コードユニット、表示幅は2だがカーソル位置(文字インデックス)は1つ分進む
          var (buf, pos) = TextInputBuffer.Insert("A", 1, '漢');
          Assert.Equal("A漢", buf);
          Assert.Equal(2, pos);
      }

      [Fact]
      public void Backspace_AtEnd_RemovesLastChar()
      {
          var (buf, pos) = TextInputBuffer.Backspace("abc", 3);
          Assert.Equal("ab", buf);
          Assert.Equal(2, pos);
      }

      [Fact]
      public void Backspace_AtStart_NoOp()
      {
          var (buf, pos) = TextInputBuffer.Backspace("abc", 0);
          Assert.Equal("abc", buf);
          Assert.Equal(0, pos);
      }

      [Fact]
      public void Backspace_AtMiddle_RemovesCharBeforeCursor()
      {
          var (buf, pos) = TextInputBuffer.Backspace("漢字test", 2);
          Assert.Equal("字test", buf);
          Assert.Equal(1, pos);
      }

      [Fact]
      public void Delete_AtMiddle_RemovesCharAtCursor()
      {
          var (buf, pos) = TextInputBuffer.Delete("abc", 1);
          Assert.Equal("ac", buf);
          Assert.Equal(1, pos); // カーソル位置自体は動かない
      }

      [Fact]
      public void Delete_AtEnd_NoOp()
      {
          var (buf, pos) = TextInputBuffer.Delete("abc", 3);
          Assert.Equal("abc", buf);
          Assert.Equal(3, pos);
      }

      [Fact]
      public void MoveLeft_DecrementsCursor()
      {
          Assert.Equal(2, TextInputBuffer.MoveLeft("abc", 3));
      }

      [Fact]
      public void MoveLeft_AtStart_ClampsToZero()
      {
          Assert.Equal(0, TextInputBuffer.MoveLeft("abc", 0));
      }

      [Fact]
      public void MoveRight_IncrementsCursor()
      {
          Assert.Equal(2, TextInputBuffer.MoveRight("abc", 1));
      }

      [Fact]
      public void MoveRight_AtEnd_ClampsToLength()
      {
          Assert.Equal(3, TextInputBuffer.MoveRight("abc", 3));
      }

      [Fact]
      public void Home_ReturnsZero()
      {
          Assert.Equal(0, TextInputBuffer.Home("abc", 2));
      }

      [Fact]
      public void End_ReturnsLength()
      {
          Assert.Equal(3, TextInputBuffer.End("abc", 1));
      }

      // ── CJK混在時の表示幅計算(カーソルの「画面上の列位置」算出) ──────────

      [Fact]
      public void DisplayColumnAtCursor_AsciiOnly_EqualsCursorPos()
      {
          Assert.Equal(3, TextInputBuffer.DisplayColumnAtCursor("abc", 3));
      }

      [Fact]
      public void DisplayColumnAtCursor_CJKBeforeCursor_CountsDoubleWidth()
      {
          // "漢字abc" でカーソルが 'a' の直前(index=2)にあるとき、表示列は 2*2=4
          Assert.Equal(4, TextInputBuffer.DisplayColumnAtCursor("漢字abc", 2));
      }

      [Fact]
      public void DisplayColumnAtCursor_MixedContent_CountsPerCharWidth()
      {
          // "a漢b" でカーソルが末尾(index=3)のとき、表示列は 1(a) + 2(漢) + 1(b) = 4
          Assert.Equal(4, TextInputBuffer.DisplayColumnAtCursor("a漢b", 3));
      }

      [Fact]
      public void DisplayColumnAtCursor_CursorAtStart_ReturnsZero()
      {
          Assert.Equal(0, TextInputBuffer.DisplayColumnAtCursor("漢字", 0));
      }
  }
  ```

- [ ] 3.2 `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter TextInputBufferTests` を実行し、`TextInputBuffer` 未定義のコンパイルエラーで失敗することを確認する。

- [ ] 3.3 `Program.cs` に `TextInputBuffer` 静的クラスを追加する。挿入位置: `DialogLayout`(Task 2)の直後。

  ```csharp
  static class TextInputBuffer
  {
      public static (string Buffer, int CursorPos) Insert(string buffer, int cursorPos, char ch)
      {
          int pos = Math.Clamp(cursorPos, 0, buffer.Length);
          string newBuffer = buffer.Insert(pos, ch.ToString());
          return (newBuffer, pos + 1);
      }

      public static (string Buffer, int CursorPos) InsertString(string buffer, int cursorPos, string text)
      {
          int pos = Math.Clamp(cursorPos, 0, buffer.Length);
          string newBuffer = buffer.Insert(pos, text);
          return (newBuffer, pos + text.Length);
      }

      public static (string Buffer, int CursorPos) Backspace(string buffer, int cursorPos)
      {
          int pos = Math.Clamp(cursorPos, 0, buffer.Length);
          if (pos == 0) return (buffer, pos);
          string newBuffer = buffer.Remove(pos - 1, 1);
          return (newBuffer, pos - 1);
      }

      public static (string Buffer, int CursorPos) Delete(string buffer, int cursorPos)
      {
          int pos = Math.Clamp(cursorPos, 0, buffer.Length);
          if (pos >= buffer.Length) return (buffer, pos);
          string newBuffer = buffer.Remove(pos, 1);
          return (newBuffer, pos);
      }

      public static int MoveLeft(string buffer, int cursorPos)
      {
          return Math.Max(0, cursorPos - 1);
      }

      public static int MoveRight(string buffer, int cursorPos)
      {
          return Math.Min(buffer.Length, cursorPos + 1);
      }

      public static int Home(string buffer, int cursorPos)
      {
          return 0;
      }

      public static int End(string buffer, int cursorPos)
      {
          return buffer.Length;
      }

      // カーソルより手前の文字列を CharacterWidth で表示幅換算し、画面上の列位置を返す。
      public static int DisplayColumnAtCursor(string buffer, int cursorPos)
      {
          int pos = Math.Clamp(cursorPos, 0, buffer.Length);
          return CharacterWidth.GetStringWidth(buffer.Substring(0, pos));
      }
  }
  ```

  補足: `Insert`/`Backspace`/`Delete` はコードユニット(`char`)単位で操作するため、結合文字・サロゲートペア(絵文字等)を厳密には1「文字」として扱わない。IME確定後の日本語文字列(ひらがな・カタカナ・漢字は基本多言語面内で1コードユニット=1文字)はこの単純化で問題なく動作する。サロゲートペア文字(絵文字等)の編集単位分割は本 Phase のスコープ外とし、`char.IsControl` ベースの既存 `HandleSearchKeyAsync` と同水準の割り切りとする。

- [ ] 3.4 `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter TextInputBufferTests` を実行し、16件すべて Passed になることを確認する。
- [ ] 3.5 `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 3.6 コミットする。
  ```
  git add Program.cs Zurari.Tests/TextInputBufferTests.cs
  git commit -m "feat: add TextInputBuffer pure editing helpers (TDD)"
  ```

---

### Task 4: リスト絞り込み(TDD) — `ListFilter`

`ListSelectDialogState.AllItems`/`FilterQuery` から `FilteredItems` を再計算し、`Selected` をクランプするロジックを純関数として切り出す。

**Files:**
- Create: `Zurari.Tests/ListFilterTests.cs`
- Modify: `Program.cs`

- [ ] 4.1 `Zurari.Tests/ListFilterTests.cs` を新規作成する。

  ```csharp
  using Xunit;

  public class ListFilterTests
  {
      [Fact]
      public void Recompute_EmptyQuery_ReturnsAllItems()
      {
          var all = new List<string> { "apple.txt", "banana.txt", "報告書.pdf" };
          var result = ListFilter.Recompute(all, "", regexMode: false, migemo: null);
          Assert.Equal(3, result.Count);
      }

      [Fact]
      public void Recompute_LiteralQuery_FiltersCaseInsensitive()
      {
          var all = new List<string> { "Report_2025.pdf", "budget.xlsx", "Reportcard.doc" };
          var result = ListFilter.Recompute(all, "report", regexMode: false, migemo: null);
          Assert.Equal(2, result.Count);
          Assert.Contains("Report_2025.pdf", result);
          Assert.Contains("Reportcard.doc", result);
      }

      [Fact]
      public void Recompute_NoMatch_ReturnsEmptyList()
      {
          var all = new List<string> { "a.txt", "b.txt" };
          var result = ListFilter.Recompute(all, "zzz", regexMode: false, migemo: null);
          Assert.Empty(result);
      }

      [Fact]
      public void Recompute_PreservesOriginalOrder()
      {
          var all = new List<string> { "zeta.txt", "alpha.txt", "zed.txt" };
          var result = ListFilter.Recompute(all, "z", regexMode: false, migemo: null);
          Assert.Equal(new List<string> { "zeta.txt", "zed.txt" }, result);
      }

      // ── ClampSelected ──────────────────────────────────────────────

      [Fact]
      public void ClampSelected_WithinRange_Unchanged()
      {
          Assert.Equal(2, ListFilter.ClampSelected(selected: 2, filteredCount: 5));
      }

      [Fact]
      public void ClampSelected_BeyondRange_ClampsToLastIndex()
      {
          Assert.Equal(4, ListFilter.ClampSelected(selected: 10, filteredCount: 5));
      }

      [Fact]
      public void ClampSelected_EmptyList_ReturnsZero()
      {
          Assert.Equal(0, ListFilter.ClampSelected(selected: 3, filteredCount: 0));
      }

      [Fact]
      public void ClampSelected_Negative_ClampsToZero()
      {
          Assert.Equal(0, ListFilter.ClampSelected(selected: -1, filteredCount: 5));
      }
  }
  ```

- [ ] 4.2 `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter ListFilterTests` を実行し、`ListFilter` 未定義のコンパイルエラーで失敗することを確認する。

- [ ] 4.3 `Program.cs` に `ListFilter` 静的クラスを追加する。挿入位置: `TextInputBuffer`(Task 3)の直後。

  ```csharp
  static class ListFilter
  {
      public static List<string> Recompute(List<string> allItems, string query, bool regexMode, MigemoProvider? migemo)
      {
          if (string.IsNullOrEmpty(query))
              return new List<string>(allItems);

          var result = new List<string>();
          foreach (string item in allItems)
          {
              if (SearchHelper.MatchesSearchQuery(item, query, regexMode, migemo))
                  result.Add(item);
          }
          return result;
      }

      public static int ClampSelected(int selected, int filteredCount)
      {
          if (filteredCount <= 0) return 0;
          return Math.Clamp(selected, 0, filteredCount - 1);
      }
  }
  ```

- [ ] 4.4 `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter ListFilterTests` を実行し、8件すべて Passed になることを確認する。
- [ ] 4.5 `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 4.6 コミットする。
  ```
  git add Program.cs Zurari.Tests/ListFilterTests.cs
  git commit -m "feat: add ListFilter.Recompute/ClampSelected pure helpers (TDD)"
  ```

---

### Task 5: ダイアログを開く API — `ShowConfirm` / `ShowTextInput` / `ShowListSelect`

**Files:**
- Modify: `Program.cs`

- [ ] 5.1 `Program.cs` に3つの静的メソッドを追加する。挿入位置: `ListFilter`(Task 4)の直後。API 契約(spec/ガイド)のシグネチャを厳守する。

  ```csharp
  static void ShowConfirm(string title, string message, Action<bool?> onResult)
  {
      State.Dialog = new ConfirmDialogState
      {
          Title = title,
          Message = message,
          OnResult = onResult,
      };
  }

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

  static void ShowListSelect(string title, IReadOnlyList<string> items, Action<string?> onResult)
  {
      var allItems = new List<string>(items);
      State.Dialog = new ListSelectDialogState
      {
          Title = title,
          AllItems = allItems,
          FilteredItems = new List<string>(allItems),
          FilterQuery = "",
          Selected = 0,
          OnResult = onResult,
      };
  }

  // 開いているダイアログを閉じ、結果コールバックを呼ぶ共通クローズ処理。
  // 呼び出し元(HandleDialogKeyAsync)が確定/キャンセル時に呼ぶ。
  static void CloseConfirmDialog(ConfirmDialogState dialog, bool? result)
  {
      State.Dialog = null;
      dialog.OnResult(result);
  }

  static void CloseTextInputDialog(TextInputDialogState dialog, string? result)
  {
      State.Dialog = null;
      dialog.OnResult(result);
  }

  static void CloseListSelectDialog(ListSelectDialogState dialog, string? result)
  {
      State.Dialog = null;
      dialog.OnResult(result);
  }
  ```

  補足: `State.Dialog = null` を**先に**行ってから `OnResult` を呼ぶ順序にする。これは、コールバック内で別のダイアログを即座に開く実装(例: 衝突解決 → リネームのテキスト入力を続けて開く、Phase 7 で使用)が `State.Dialog` を安全に上書きできるようにするための順序である。

- [ ] 5.2 `dotnet build Zurari.csproj` で exit 0 を確認する(まだ呼び出し元がないため、未使用警告のみで失敗はしない想定)。
- [ ] 5.3 コミットする。
  ```
  git add Program.cs
  git commit -m "feat: add ShowConfirm/ShowTextInput/ShowListSelect dialog-open APIs"
  ```

---

### Task 6: `HandleDialogKeyAsync` の実装

3種のダイアログの入力処理を1つの関数に集約する。`ConfirmDialogState` は同期処理のみだが、`Main` からの呼び出しを統一するため `async Task` シグネチャにする(Leap/Search と同じパターン)。

**Files:**
- Modify: `Program.cs`

- [ ] 6.1 `HandleDialogKeyAsync(Pane pane, ConsoleKeyInfo key)` を追加する。挿入位置: `HandleSearchKeyAsync`(`Program.cs:3159` 付近、Phase 1 リファクタ後は `Pane pane` を先頭引数に取る形になっている)の直前。

  ```csharp
  static Task HandleDialogKeyAsync(Pane pane, ConsoleKeyInfo key)
  {
      DialogState? dialog = State.Dialog;
      if (dialog == null) return Task.CompletedTask;

      switch (dialog)
      {
          case ConfirmDialogState confirm:
              HandleConfirmDialogKey(confirm, key);
              break;

          case TextInputDialogState textInput:
              HandleTextInputDialogKey(textInput, key);
              break;

          case ListSelectDialogState listSelect:
              HandleListSelectDialogKey(listSelect, key);
              break;
      }

      return Task.CompletedTask;
  }

  static void HandleConfirmDialogKey(ConfirmDialogState dialog, ConsoleKeyInfo key)
  {
      if (key.Key == ConsoleKey.Escape)
      {
          CloseConfirmDialog(dialog, null);
          return;
      }

      if (key.Key == ConsoleKey.Enter || key.KeyChar is 'y' or 'Y')
      {
          CloseConfirmDialog(dialog, true);
          return;
      }

      if (key.KeyChar is 'n' or 'N')
      {
          CloseConfirmDialog(dialog, false);
          return;
      }

      // Everything else is swallowed.
  }

  static void HandleTextInputDialogKey(TextInputDialogState dialog, ConsoleKeyInfo key)
  {
      if (key.Key == ConsoleKey.Escape)
      {
          CloseTextInputDialog(dialog, null);
          return;
      }

      if (key.Key == ConsoleKey.Enter)
      {
          CloseTextInputDialog(dialog, dialog.Buffer);
          return;
      }

      if (key.Key == ConsoleKey.Backspace)
      {
          (dialog.Buffer, dialog.CursorPos) = TextInputBuffer.Backspace(dialog.Buffer, dialog.CursorPos);
          return;
      }

      if (key.Key == ConsoleKey.Delete)
      {
          (dialog.Buffer, dialog.CursorPos) = TextInputBuffer.Delete(dialog.Buffer, dialog.CursorPos);
          return;
      }

      if (key.Key == ConsoleKey.LeftArrow)
      {
          dialog.CursorPos = TextInputBuffer.MoveLeft(dialog.Buffer, dialog.CursorPos);
          return;
      }

      if (key.Key == ConsoleKey.RightArrow)
      {
          dialog.CursorPos = TextInputBuffer.MoveRight(dialog.Buffer, dialog.CursorPos);
          return;
      }

      if (key.Key == ConsoleKey.Home)
      {
          dialog.CursorPos = TextInputBuffer.Home(dialog.Buffer, dialog.CursorPos);
          return;
      }

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

      // Everything else is swallowed.
  }

  static void HandleListSelectDialogKey(ListSelectDialogState dialog, ConsoleKeyInfo key)
  {
      if (key.Key == ConsoleKey.Escape)
      {
          CloseListSelectDialog(dialog, null);
          return;
      }

      if (key.Key == ConsoleKey.Enter)
      {
          string? result = (dialog.Selected >= 0 && dialog.Selected < dialog.FilteredItems.Count)
              ? dialog.FilteredItems[dialog.Selected]
              : null;
          CloseListSelectDialog(dialog, result);
          return;
      }

      if (key.Key == ConsoleKey.DownArrow)
      {
          dialog.Selected = ListFilter.ClampSelected(dialog.Selected + 1, dialog.FilteredItems.Count);
          return;
      }

      if (key.Key == ConsoleKey.UpArrow)
      {
          dialog.Selected = ListFilter.ClampSelected(dialog.Selected - 1, dialog.FilteredItems.Count);
          return;
      }

      if (key.Key == ConsoleKey.Backspace)
      {
          if (dialog.FilterQuery.Length > 0)
          {
              dialog.FilterQuery = dialog.FilterQuery[..^1];
              RecomputeListSelectFilter(dialog);
          }
          return;
      }

      // 絞り込みクエリへの文字追加: HandleSearchKeyAsync と同一の判定。
      if (!char.IsControl(key.KeyChar))
      {
          dialog.FilterQuery += key.KeyChar;
          RecomputeListSelectFilter(dialog);
          return;
      }

      // Everything else is swallowed.
  }

  // 対象リストは高々数百件のため、キー入力ごとに全件再評価する(デバウンスなし。spec「ダイアログ設計」節)。
  static void RecomputeListSelectFilter(ListSelectDialogState dialog)
  {
      dialog.FilteredItems = ListFilter.Recompute(dialog.AllItems, dialog.FilterQuery, regexMode: false, _migemo);
      dialog.Selected = ListFilter.ClampSelected(dialog.Selected, dialog.FilteredItems.Count);
  }
  ```

  補足(上下矢印での選択移動): spec は「↑/↓ で選択項目の移動」とだけ定めており、循環(末尾→先頭)の要否は明記していない。`ListFilter.ClampSelected` によるクランプ(末尾・先頭で止まる、循環しない)を採用する。循環動作が必要になった場合は「未解決事項」に記録すること(挙動を変えたい場合は別途仕様判断が必要)。

  補足(`y`/`Y`/`n`/`N` の判定に `ConsoleKeyInfo.KeyChar` を使う理由): `ConsoleKey.Y`/`ConsoleKey.N` を使うと `Shift` の有無に関わらず同じ `ConsoleKey` 値になるため、大文字小文字を区別せず Y/N として扱いたい本ダイアログの用途には `KeyChar` によるパターンマッチ(`is 'y' or 'Y'`)の方が意図が明確である。

- [ ] 6.2 `dotnet build Zurari.csproj` を実行する。**期待結果**: exit 0。`Pane pane` 引数を受け取るが本体では未使用(将来 Phase でペイン依存のダイアログが増える可能性への配慮、および `Main` からの呼び出し規約を Leap/Search と揃えるための引数)であるため、未使用引数の警告が出ても無視してよい。
- [ ] 6.3 コミットする。
  ```
  git add Program.cs
  git commit -m "feat: add HandleDialogKeyAsync with Confirm/TextInput/ListSelect key handling"
  ```

---

### Task 7: メインループの入力分岐を統合する

**Files:**
- Modify: `Program.cs`

- [ ] 7.1 `Main`(`Program.cs:1688` 付近)内のキー入力分岐を、次の優先順位1本に書き換える。現状(Phase 1 リファクタ後)は次の形になっているはずである。

  ```csharp
              ConsoleKeyInfo key = Console.ReadKey(true);

              if (FocusedPane.Leap.Active)
              {
                  await HandleLeapKeyAsync(FocusedPane, key);
                  continue;
              }

              if (FocusedPane.Search.Active)
              {
                  await HandleSearchKeyAsync(FocusedPane, key);
                  continue;
              }

              switch (key.KeyChar)
              {
                  case (char)27: // Esc
                      return;

                  default:
                      await HandleSpecialKeyAsync(FocusedPane, key);
                      break;
              }
  ```

  これを次のように書き換える。

  ```csharp
              ConsoleKeyInfo key = Console.ReadKey(true);

              if (State.Dialog != null)
              {
                  await HandleDialogKeyAsync(FocusedPane, key);
                  continue;
              }

              if (FocusedPane.Leap.Active)
              {
                  await HandleLeapKeyAsync(FocusedPane, key);
                  continue;
              }

              if (FocusedPane.Search.Active)
              {
                  await HandleSearchKeyAsync(FocusedPane, key);
                  continue;
              }

              switch (key.KeyChar)
              {
                  case (char)27: // Esc
                      return;

                  default:
                      await HandleSpecialKeyAsync(FocusedPane, key);
                      break;
              }
  ```

  注意: `case (char)27: return;` は「ダイアログが開いていない通常状態での Esc = 即終了」であり、この分岐は `State.Dialog != null` チェックより**後**にあるため、ダイアログが開いている間は Esc を押しても `HandleDialogKeyAsync` 側(`ConfirmDialogState`/`TextInputDialogState`/`ListSelectDialogState` いずれも Esc でキャンセルしてダイアログを閉じるだけ)が先に処理し、アプリ終了には至らない。この優先順位の逆転が起きていないことを次のステップで確認する。

  もし実際の `Main` の形が上記の「現状」と異なる場合(例えば `State.Leap.Active` のように `Pane` 引数化前の参照が残っている場合)は、Phase 1/2 が未完了である可能性が高い。前提条件の再検証に戻り、それでも食い違う場合は「未解決事項」に実際のコードを記録して停止する。

- [ ] 7.2 メインループの無入力時デバウンス処理(`if (!Console.KeyAvailable) { ... }` ブロック)を確認する。`State.Dialog != null` のときは Leap/Search の再計算デバウンスを走らせないようにするため、ブロック先頭に早期リターン相当のガードを追加する。

  ```csharp
              if (!Console.KeyAvailable)
              {
                  if (State.Dialog == null && FocusedPane.Leap.Active
                      && FocusedPane.Leap.NeedsRecompute
                      && (DateTime.UtcNow - FocusedPane.Leap.LastInputTime).TotalMilliseconds >= NavigationDebounceMs)
                  {
                      FocusedPane.Leap.NeedsRecompute = false;
                      ApplyLeapFilter(FocusedPane);
                      await TryLeapAutoEnterAsync(FocusedPane);
                  }
                  else if (State.Dialog == null && FocusedPane.Search.Active
                      && FocusedPane.Search.NeedsRecompute
                      && (DateTime.UtcNow - FocusedPane.Search.LastInputTime).TotalMilliseconds >= 300)
                  {
                      await RecomputeMatchesAsync(FocusedPane);
                      UpdateHorizontalScroll(FocusedPane);
                      await RebuildRightSideAsync(FocusedPane, FocusedPane.ActiveColumn);
                      if (FocusedPane.Preview.IsVisible) StartPreviewLoad(FocusedPane);
                  }
                  System.Threading.Thread.Sleep(50);
                  continue;
              }
  ```

  実際の関数名・引数の形(`Pane` 引数の有無・順序)は Phase 1 の実装に合わせること。ここでの要点は「`State.Dialog != null` の間は Leap/Search のデバウンス再計算を止める」という1点のみであり、それ以外は既存コードの構造を変えない。

- [ ] 7.3 `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 7.4 `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、既存テストが全緑のままであることを確認する。
- [ ] 7.5 コミットする。
  ```
  git add Program.cs
  git commit -m "feat: route dialog input first in main loop, ahead of Leap/Search"
  ```

---

### Task 8: ダイアログ内容の組み立てとオーバーレイ合成 — `ComposeDialogIntoFrame`

ダイアログの中央ボックスは、パス行・ステータス行を含む画面全体に重なってよいオーバーレイである。`BuildFrame` は `Line[]`(`height - 2` 行分)を文字列へレンダリングした後、パス行・ステータス行を `string[] frame` に直接上書きする構造になっている(`Program.cs:3457`-`3508` 付近)。ダイアログのボックスがこの2行にも重なるケースがあるため(画面中央付近に大きなリスト選択ダイアログが出た場合など)、オーバーレイの書き込みは `Line[]` の段階ではなく、**`frame[]` が完成した最後の瞬間**に行う。ガイドが示す `DrawDialogToLines(Line[] lines, ...)` という関数シグネチャは、この理由により本計画では `ComposeDialogIntoFrame(string[] frame, DialogState dialog, int width, int height)` という「完成済み `frame[]` を書き換える」形に置き換える。理由は本文に明記した通りであり、実行セッションが追加調査・判断をする必要はない。

**Files:**
- Modify: `Program.cs`

- [ ] 8.1 `Program.cs` に、ダイアログ種別ごとの内容行(文字列のリスト)を組み立てる `BuildDialogContentLines` と、タイトル入り上端枠線を組み立てる `BuildTitledTopBorder` を追加する。挿入位置: `DrawPreviewToLines`(`Program.cs:3715` 付近)の直後。

  ```csharp
  static string BuildTitledTopBorder(string title, int innerWidth)
  {
      string label = string.IsNullOrEmpty(title) ? "" : $"┤ {title} ├";
      int labelWidth = CharacterWidth.GetStringWidth(label);
      if (labelWidth > innerWidth)
      {
          label = "";
          labelWidth = 0;
      }
      int leftDashes = Math.Max(1, (innerWidth - labelWidth) / 2);
      int rightDashes = Math.Max(1, innerWidth - labelWidth - leftDashes);
      return "┌" + new string('─', leftDashes) + label + new string('─', rightDashes) + "┐";
  }

  // ダイアログ種別ごとの内容行を組み立てる。カーソル位置は反転スペース(ANSI SGR 7)を
  // テキスト中に挿入することで表現する(コンソールカーソル自体は非表示のため)。
  static List<string> BuildDialogContentLines(DialogState dialog)
  {
      switch (dialog)
      {
          case ConfirmDialogState confirm:
              return new List<string>
              {
                  confirm.Message,
                  "",
                  "[Y]es  [N]o  [Esc]キャンセル",
              };

          case TextInputDialogState textInput:
          {
              string beforeCursor = textInput.Buffer.Substring(0, textInput.CursorPos);
              string atAndAfterCursor = textInput.Buffer.Substring(textInput.CursorPos);
              string displayLine = beforeCursor + "\x1b[7m \x1b[0m" + atAndAfterCursor;
              return new List<string>
              {
                  displayLine,
                  "",
                  "[Enter]確定  [Esc]キャンセル",
              };
          }

          case ListSelectDialogState listSelect:
          {
              var lines = new List<string>
              {
                  "/" + listSelect.FilterQuery + "\x1b[7m \x1b[0m",
                  "",
              };

              if (listSelect.FilteredItems.Count == 0)
              {
                  lines.Add("(該当なし)");
              }
              else
              {
                  const int maxVisibleRows = 12;
                  int total = listSelect.FilteredItems.Count;
                  int visibleRows = Math.Min(maxVisibleRows, total);

                  int scrollStart = 0;
                  if (listSelect.Selected >= visibleRows)
                      scrollStart = listSelect.Selected - visibleRows + 1;
                  scrollStart = Math.Min(scrollStart, Math.Max(0, total - visibleRows));

                  for (int i = 0; i < visibleRows; i++)
                  {
                      int idx = scrollStart + i;
                      if (idx >= total) break;
                      string prefix = idx == listSelect.Selected ? "> " : "  ";
                      string itemText = prefix + listSelect.FilteredItems[idx];
                      if (idx == listSelect.Selected)
                          itemText = "\x1b[7m" + itemText + "\x1b[0m";
                      lines.Add(itemText);
                  }
              }

              lines.Add("");
              lines.Add("[↑↓]選択  [Enter]確定  [Esc]キャンセル");
              return lines;
          }

          default:
              return new List<string> { "" };
      }
  }
  ```

  補足(ANSI エスケープと表示幅計算の整合): `CharacterWidth.GetStringWidth`/`SmartTruncate`/`PadToWidth` は ANSI エスケープシーケンス自体を特別扱いしない。これは既存の `BuildSearchStatusBar`(`Program.cs:617` 付近)も同じ前提で書かれている既存の制約であり、本 Phase で新しく持ち込むものではない。切り詰めが発生する行では ANSI 装飾を保持したまま正確に幅計算するのが複雑になるため、次のステップ(`ComposeDialogIntoFrame`)ではエスケープを除いたプレーン文字列で幅を測り、切り詰めが実際に発生した行だけプレーンテキストにフォールバックする(強調表示が失われるのみで実害はない)。

- [ ] 8.2 `BuildDialogContentLines` の直後に `ComposeDialogIntoFrame` と、それが使う補助関数を追加する。

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

  // ANSI付きの行を innerWidth ちょうどの表示幅に整形する。切り詰めが必要な行は
  // ANSI装飾を落としてプレーンテキストで切り詰め・パディングする(既存のステータスバー実装と
  // 同水準の割り切り。補足はTask 8.1参照)。
  static string FitRowToWidth(string rowText, int innerWidth)
  {
      string plain = StripAnsiForWidth(rowText);
      int plainWidth = CharacterWidth.GetStringWidth(plain);
      if (plainWidth <= innerWidth && plain.Length == rowText.Length)
      {
          // ANSIを含まない、かつ幅に収まる行はそのままパディングして返す。
          return rowText + new string(' ', innerWidth - plainWidth);
      }
      string truncated = CharacterWidth.SmartTruncate(plain, innerWidth);
      return CharacterWidth.PadToWidth(truncated, innerWidth);
  }

  // frame[y] の表示列 x から displayWidth 分を content で上書きする。
  // CJK混在時、文字インデックスと表示列インデックスが一致しないため、
  // 表示幅ベースで文字単位に分解してから置換区間を決定する。
  static void OverwriteFrameRow(string[] frame, int y, int x, string content, int frameWidth, int frameHeight)
  {
      if (y < 0 || y >= frameHeight) return;

      string baseLine = frame[y];
      var cells = new List<string>();
      var widths = new List<int>();
      foreach (char c in baseLine)
      {
          cells.Add(c.ToString());
          widths.Add(CharacterWidth.GetCharWidth(c));
      }

      var startCol = new int[cells.Count];
      int col = 0;
      for (int i = 0; i < cells.Count; i++)
      {
          startCol[i] = col;
          col += widths[i];
      }
      int totalWidth = col;

      int displayWidth = CharacterWidth.GetStringWidth(StripAnsiForWidth(content));
      if (x < 0 || x >= totalWidth || displayWidth <= 0) return;

      int startIdx = -1, endIdx = -1; // endIdx は exclusive
      for (int i = 0; i < cells.Count; i++)
      {
          if (startCol[i] >= x && startIdx < 0) startIdx = i;
          if (startCol[i] < x + displayWidth) endIdx = i + 1;
      }
      if (startIdx < 0) { frame[y] = baseLine; return; }
      if (endIdx < startIdx) endIdx = startIdx;
      endIdx = Math.Min(endIdx, cells.Count);

      int removedWidth = 0;
      for (int i = startIdx; i < endIdx; i++)
          removedWidth += widths[i];

      string replacement = content;
      int replacementWidth = CharacterWidth.GetStringWidth(StripAnsiForWidth(replacement));
      if (replacementWidth < removedWidth)
          replacement += new string(' ', removedWidth - replacementWidth);

      cells.RemoveRange(startIdx, endIdx - startIdx);
      cells.Insert(startIdx, replacement);

      frame[y] = string.Concat(cells);
  }

  // ANSI エスケープシーケンス(色・反転等)を取り除いた表示幅計算用のプレーン文字列を返す。
  static string StripAnsiForWidth(string text)
  {
      return System.Text.RegularExpressions.Regex.Replace(text, "\x1b\\[[0-9;]*m", "");
  }
  ```

- [ ] 8.3 Phase 2 で用意された空フック `DrawDialogOverlay(string[] frame, int width, int height)`(`BuildFrame` の `return frame;` 直前から呼ばれている no-op)の中身を実装する。`BuildFrame` 側は変更しない(呼び出しは Phase 2 で配線済み。オーバーレイの呼び出し点は1箇所に保つ)。

  ```csharp
  // オーバーレイ層: ダイアログ(常に最後に重ね書きする。パス行・ステータス行の上にも表示されてよい)
  static void DrawDialogOverlay(string[] frame, int width, int height)
  {
      if (State.Dialog == null) return;
      ComposeDialogIntoFrame(frame, State.Dialog, width, height);
  }
  ```

  もし Phase 2 実装後のコードに `DrawDialogOverlay` が存在しない、またはシグネチャが異なる場合は、実物のフックに同じ内容を実装する(`BuildFrame` に新たな呼び出し箇所を増やさないこと)。

- [ ] 8.4 `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 8.5 `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、既存テスト+新規3ファイル(`DialogLayoutTests`/`TextInputBufferTests`/`ListFilterTests`)がすべて Passed であることを確認する。
- [ ] 8.6 コミットする。
  ```
  git add Program.cs
  git commit -m "feat: compose dialog overlay onto finished frame in BuildFrame"
  ```

---

### Task 9: 手動確認用の一時デバッグホットキーを追加する

3種のダイアログを実機で単独確認するため、`HandleSpecialKeyAsync` の未使用キーに一時的なデバッグホットキーを割り当てる。これは Phase 4 で実際の機能(削除確認・リネーム・ヒストリ選択等)が `ShowConfirm`/`ShowTextInput`/`ShowListSelect` を呼ぶようになった時点で不要になる**一時的なコード**であり、本 Phase の完了条件(Task 10 の手動確認)のためだけに存在する。Task 10 の完了後、本 Task で追加したホットキーは Task 11 で削除する。

**Files:**
- Modify: `Program.cs`

- [ ] 9.1 `F9`/`F10`/`F11` が既存コードで未使用であることを確認する。

  ```
  grep -n "ConsoleKey.F9\|ConsoleKey.F10\|ConsoleKey.F11" Program.cs
  ```

  期待: 0件ヒット。

- [ ] 9.2 `HandleSpecialKeyAsync(Pane pane, ConsoleKeyInfo key)` の `switch (key.Key)` に、`case ConsoleKey.F5:` の直後として次の3 case を追加する。

  ```csharp
              case ConsoleKey.F9: // TEMP DEBUG (Phase 3 manual verification only — removed in Task 10)
                  ShowConfirm("デバッグ確認", "これはテスト用の確認ダイアログです。続行しますか?", result =>
                  {
                      _lastErrorMessage = $"[F9] result = {(result == null ? "null(Esc)" : result.ToString())}";
                  });
                  break;

              case ConsoleKey.F10: // TEMP DEBUG (Phase 3 manual verification only — removed in Task 10)
                  ShowTextInput("デバッグ入力", "初期値123", result =>
                  {
                      _lastErrorMessage = result == null ? "[F10] キャンセル" : $"[F10] 確定: {result}";
                  });
                  break;

              case ConsoleKey.F11: // TEMP DEBUG (Phase 3 manual verification only — removed in Task 10)
                  ShowListSelect("デバッグ選択", new List<string>
                  {
                      "りんご.txt", "banana.txt", "報告書_2025.pdf", "budget.xlsx",
                      "テスト.docx", "sample_data.csv", "画像フォルダ/", "旅行の写真.jpg",
                  }, result =>
                  {
                      _lastErrorMessage = result == null ? "[F11] キャンセル" : $"[F11] 選択: {result}";
                  });
                  break;
  ```

- [ ] 9.3 `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 9.4 コミットする。
  ```
  git add Program.cs
  git commit -m "test: add temporary F9/F10/F11 debug hotkeys for Phase 3 manual dialog verification"
  ```

---

### Task 10: 手動確認(実機・IME 検証を含む)

**Files:** なし(コード変更なし。確認のみ)

- [ ] 10.1 `dotnet run` でアプリを起動する。

- [ ] 10.2 **確認ダイアログの確認**: `F9` を押す。
  - 期待画面: 画面中央に罫線ボックスが表示され、上端に `┤ デバッグ確認 ├` のタイトル、内部に「これはテスト用の確認ダイアログです。続行しますか?」、その下に「[Y]es [N]o [Esc]キャンセル」が表示される。
  - `Y` を押す。ダイアログが閉じ、ステータス行に `[F9] result = True` が表示される。
  - 再度 `F9` → `N` を押す。ステータス行に `[F9] result = False` が表示される。
  - 再度 `F9` → `Esc` を押す。ステータス行に `[F9] result = null(Esc)` が表示される。
  - 再度 `F9` → `Enter` を押す。`Y` と同じ扱いで `True` になることを確認する。

- [ ] 10.3 **テキスト入力ダイアログの確認(ASCII)**: `F10` を押す。
  - 期待画面: ボックス内に `初期値123` が表示され、カーソル位置は末尾(文字列の直後に反転スペースの疑似カーソル)。
  - `Backspace` を3回押す。「初期値」まで削られることを確認する。
  - `Home` を押し、`abc` と入力する。先頭に `abc初期値` となることを確認する。
  - `End` を押し、`XYZ` と入力する。末尾に追加されることを確認する。
  - `←` を2回押してカーソルを移動し、`Delete` を1回押す。カーソル位置の直後の1文字が消えることを確認する。
  - `Enter` で確定する。ステータス行に `[F10] 確定: <編集後の文字列>` が表示される。
  - 再度 `F10` → 何か入力後 `Esc`。ステータス行に `[F10] キャンセル` が表示され、値が破棄されることを確認する(次に `F10` を押すと初期値 `初期値123` に戻っていること)。

- [ ] 10.4 **テキスト入力ダイアログの確認(日本語 IME、実機検証)**: `F10` を押す。
  - IME をオンにし(Windows の場合 `半角/全角` キー、または IME パッド)、ひらがなを数文字入力する(例: `にほんご`)。変換候補が表示され、`Space` で変換し `テスト太郎` のような文字列に変換して `Enter`(IME確定、ダイアログの確定ではない)で確定する。
  - 期待動作: IME 変換中の文字が画面上のダイアログ内に正しく表示され、文字化けしないこと。IME 確定後、確定した日本語文字列がバッファに正しく追加され、カーソルが文字列の末尾(または入力位置)に移動していること。
  - `Backspace` を1回押す。日本語1文字(例: `郎`)が1文字単位で削除されることを確認する(サロゲートペアでない通常の漢字・ひらがな・カタカナが対象。絵文字等のサロゲートペア文字は本 Phase のスコープ外につき未検証でよい)。
  - ダイアログの `Enter`(ダイアログ確定。IME確定とは別)を押して確定する。ステータス行に確定後の日本語文字列が文字化けなく表示されることを確認する。
  - **既知のリスク**: `Console.ReadKey` は IME 変換中の中間状態(未確定文字列)をキーイベントとして送らない環境がある。もし変換中の文字がダイアログ内に全く表示されず、IME確定時に初めて文字列がまとめて反映される(あるいは反映されない)場合は、これは `Console.ReadKey` ベースの入力方式の既知の制約であり、`HandleSearchKeyAsync` も同じ制約を持つ(検索モードでも同じ現象が起きるはずなので、検索モード `/` でも同じ文字列を入力して同じ挙動になるか比較すること)。挙動が検索モードと異なる場合のみ、本ダイアログ実装固有のバグとして扱い、原因を調査する。同じ挙動であれば「Phase 3 のスコープ外、既存制約の追認」として扱い、完了条件を満たすものとする。

- [ ] 10.5 **リスト選択ダイアログの確認**: `F11` を押す。
  - 期待画面: 8件のリスト(`りんご.txt`, `banana.txt` 等)が表示され、先頭行が反転表示(選択中)。
  - `↓` を数回押し、選択が下に移動することを確認する。末尾で止まる(循環しない)ことを確認する。
  - `↑` を押して先頭に戻す。先頭で止まることを確認する。
  - `banana` と入力する(ローマ字、絞り込みクエリ)。リストが `banana.txt` のみに絞り込まれることを確認する。
  - `Backspace` で `banana` を全部消す。8件全件が再表示されることを確認する。
  - migemo が利用可能な環境であれば、`houkokusho`(ローマ字)と入力し、「報告書_2025.pdf」が絞り込み結果に現れることを確認する(migemo 未導入環境では一致しなくてよい。`_migemo` が `null` の場合は `SearchHelper.MatchesSearchQuery` が単純部分一致にフォールバックする)。
  - 絞り込んだ状態で `Enter` を押す。ステータス行に `[F11] 選択: <選ばれた項目>` が表示されることを確認する。
  - 再度 `F11` → `Esc`。ステータス行に `[F11] キャンセル` が表示されることを確認する。

- [ ] 10.6 **優先順位の確認(ダイアログ最優先)**: 次の手順で確認する。
  1. `F3` で Leap モードに入り、`Esc` で Leap を抜ける(通常状態に戻す)。
  2. `F9` を押して確認ダイアログを開く。ダイアログを閉じない状態で `F3`(Leap開始キー)を押す。
  - 期待動作: ダイアログが開いている間は `F3` を押してもダイアログが閉じず、Leap モードにも入らない(`HandleDialogKeyAsync` が `F3` を「その他」として無視して吸収するため、画面に変化がない)。`Esc` を押すとダイアログが閉じ、その後の `F3` で通常通り Leap モードに入れることを確認する。

- [ ] 10.7 手動確認がすべて期待通りであれば完了。期待通りでない項目があれば、原因を特定できる範囲は修正し、特定できない・仕様判断が必要なものは「未解決事項」に記録する。

---

### Task 11: 一時デバッグホットキーの削除

Task 9 で追加した `F9`/`F10`/`F11` は Task 10 の手動確認専用であり、Phase 4 で実際の機能(`d`/`r`/`h`/`b` 等)が `ShowConfirm`/`ShowTextInput`/`ShowListSelect` を呼ぶようになるため不要になる。Phase 4 開始前に本 Task で取り除き、デバッグ専用コードを残さない。

**Files:**
- Modify: `Program.cs`

- [ ] 11.1 Task 10 の手動確認がすべて完了していることを確認したうえで、`HandleSpecialKeyAsync` に Task 9.2 で追加した `case ConsoleKey.F9:` / `case ConsoleKey.F10:` / `case ConsoleKey.F11:` の3 case を削除する。

- [ ] 11.2 `grep -n "TEMP DEBUG" Program.cs` を実行し、0件ヒットになることを確認する(削除漏れがないことの確認)。

- [ ] 11.3 `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 11.4 `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、全緑であることを確認する。
- [ ] 11.5 コミットする。
  ```
  git add Program.cs
  git commit -m "chore: remove temporary F9/F10/F11 debug hotkeys after manual verification"
  ```

---

## 完了条件チェックリスト

- [x] `DialogState`/`ConfirmDialogState`/`TextInputDialogState`/`ListSelectDialogState` が `Program.cs` に実装され、`ScreenState.Dialog` に保持される。
- [x] `ShowConfirm(string, string, Action<bool?>)` / `ShowTextInput(string, string, Action<string?>)` / `ShowListSelect(string, IReadOnlyList<string>, Action<string?>)` が spec のシグネチャ通りに実装され、呼び出すと即座に返り `State.Dialog` がセットされる。
- [x] `HandleDialogKeyAsync` が3種のダイアログそれぞれの確定/キャンセル/編集キーを処理し、`Main` のメインループが「`State.Dialog != null` → `HandleDialogKeyAsync`」を最優先に呼ぶ形に統合されている。
- [x] ダイアログが開いている間、フォーカスペインの Leap/Search がアクティブであっても入力が届かない(ダイアログ最優先)。実機確認済み(Task 10.6: F9 でダイアログを開いた状態で F3 を押しても Leap に入らないことを確認)。
- [x] `CalcDialogRect` が純関数として実装され、`DialogLayoutTests.cs` の全テストが Passed。
- [x] `TextInputBuffer`(挿入/BS/Del/←→/Home/End/CJK表示幅計算)が純関数として実装され、`TextInputBufferTests.cs` の全テストが Passed。
- [x] `ListFilter.Recompute`/`ClampSelected` が `SearchHelper.MatchesSearchQuery` を再利用する形で実装され、`ListFilterTests.cs` の全テストが Passed。
- [x] テキスト入力ダイアログの文字受け取りが `HandleSearchKeyAsync` と同一の判定(`!char.IsControl(key.KeyChar)`)を使っている。
- [x] リスト選択ダイアログの絞り込みが `SearchHelper.MatchesSearchQuery(entry, query, regexMode: false, _migemo)` を使い、キー入力ごとに全件再評価している。
- [x] ダイアログの中央ボックス描画(`ComposeDialogIntoFrame`)が `CharacterWidth.GetStringWidth`/`SmartTruncate`/`PadToWidth` を使い CJK 幅を正しく扱う。
- [x] `dotnet build Zurari.csproj` が exit 0。
- [x] `dotnet test Zurari.Tests/Zurari.Tests.csproj` が全緑(既存テスト + 新規3ファイル。実績: 101件、内訳は既存69件 + DialogLayoutTests 6件 + TextInputBufferTests 18件 + ListFilterTests 8件)。
- [x] 3種のダイアログが実機で単独に開閉・確定・キャンセルできることを手動確認済み(Task 10)。ユーザーによる実機確認で F9(確認)/F10(テキスト入力)/F11(リスト選択)いずれも開閉・確定・キャンセル・カーソル移動・絞り込みが正常動作。
- [x] 日本語 IME 入力を含むテキスト入力ダイアログが実機で文字化けなく動作することを確認済み(Task 10.4)。ユーザーが実機で日本語IME入力(変換・確定・Backspaceでの1文字削除)を確認し、文字化けなしと報告。
- [x] Task 9 で追加した `F9`/`F10`/`F11` の一時デバッグホットキーが Task 11 で削除され、`grep -n "TEMP DEBUG" Program.cs` が0件であること。

## 未解決事項

- **worktree のベースブランチずれ(実行セッションが自己解決)**: `EnterWorktree` ツールは既定で `origin/<default-branch>` を起点に新規ブランチを作成するが、本リポジトリの `origin/main` は Phase 2 完了コミット群(`c9743ee`〜`e4233b4`、`DrawPaneToLines` 3層分割を含む)が未 push のまま古い状態だった。そのため作成直後の worktree には前提条件(`DrawPaneToLines`/`DrawDialogOverlay` フック等)が欠落していた。着手前に `git log --oneline main..HEAD` で差分がないことを確認したうえで `git reset --hard main`(ローカル `main`)を実行し、正しい前提の上で作業を開始した。ユーザー判断が必要な事項ではなく、実行セッションが検出・解決した実装上の前提ズレとして記録する。今後同種のセッションを始める際は、`origin/<default-branch>` が最新かどうかを事前に確認するとよい。
- **`TextInputBufferTests.Backspace_AtMiddle_RemovesCharBeforeCursor` の期待値誤り(実行セッションが自己解決)**: 計画書记載のテストは `TextInputBuffer.Backspace("漢字test", 2)` の結果を `"字test"` と期待していたが、同じく計画書に記載された参照実装(`buffer.Remove(pos - 1, 1)`)は cursorPos=2 の直前の文字(index=1 の `'字'`)を除去するため `"漢test"` が正しい。これは `MoveLeft`/`Insert` など他の全テストと整合する「ギャップ型カーソル」意味論とも一致する。計画の Task 2.4 にある「テストとコードのどちらが矛盾しているか算出式と突き合わせて判断する」という方針を Task 3 にも準用し、期待値を `"漢test"` に修正した(実装は計画通りのまま変更なし)。
- **Task 5/6 のコード挿入位置(実行セッションが自己解決)**: 計画は `ShowConfirm`/`ShowTextInput`/`ShowListSelect` 等の挿入位置を「`ListFilter`(トップレベル static クラス)の直後」としていたが、これらは `State`(`Program` クラスのメンバ、`internal static readonly ScreenState State`)を無出所参照で使うため、`ListFilter` と同じトップレベルスコープには置けない(`State` が解決できずコンパイルエラーになる)。実際には `Program` クラス内、`HandleSearchKeyAsync` の直前(Task 6 の挿入位置と同じ場所)にまとめて配置した。処理内容・シグネチャは計画のコードそのまま。
- **`OverwriteFrameRow` の ANSI エスケープ未対応バグ(実行セッション後、ユーザーの実機確認で発覚・修正)**: Task 10 の手動確認で、ダイアログボックスがディレクトリ一覧(青色 ANSI 装飾つき)に重なる行で、罫線が青く着色される・列がズレる・余分な `|` が出るなどの表示崩れが報告された。原因は `CharacterWidth.GetCharWidth('\x1b')` が1を返すため、`OverwriteFrameRow` の1文字ずつのセル分割ロジックが ANSI エスケープシーケンスの各バイトを表示幅1の可視文字として数えてしまい、桁計算がズレたうえ置換範囲がエスケープシーケンスの途中で切れて色コードの対応が壊れることだった(`FitRowToWidth` は元々ダイアログ自身のコンテンツ側の ANSI 除去には対応していたが、上書き先である `frame[y]` 側の ANSI は未対応だった)。1回目の修正では対象行全体を `StripAnsiForWidth` でプレーン化する簡易対応を行ったが、ダイアログが重なる行全体の色装飾(ボックス外側の部分も含む)が失われる副作用があり、ユーザーに確認したところ「ボックス外側の色は保持してほしい」との回答だったため、2回目の修正でエスケープシーケンスを直後の可視文字に付随させたセル単位で走査し、置換範囲外の元の装飾を保持しつつ置換範囲の前後に明示的なリセット(`\x1b[0m`)を挿入する実装に置き換えた(コミット `1d986d4` → `3059fb4`)。ユーザーの実機再確認で色保持・罫線・カーソル位置とも正常動作を確認済み。計画書には記載のなかった追加修正のため、ここに記録する。
