# 上下2ペイン化とコピー/移動機能 — 設計スペック

**日付**: 2026-07-06
**ステータス**: 設計確定、実装フェーズ着手待ち

---

## 背景と現状

zurari は C#/.NET 8 で自作した TUI ファイルマネージャである。Miller カラム型のナビゲーションを持ち、Windows を主対象として CJK 幅処理を自前実装している。Terminal.Gui のような外部 TUI フレームワークは採用しない方針であり、ほぼ全コード(約3800行)が単一の `Program.cs` に収まっている。

現状の主要構造は次の通り。

- **`Column`**: `Path`, `Entries`(`List<string>`), `Selected`, `ScrollOffset` および非同期読み込み状態(`IsLoading`, `ReadCts` 等)を持つ。1つのカラム = 1つのディレクトリの一覧に対応。
- **`ScreenState`**: `ActiveColumn`, `HorizontalScroll`, `PrevFrame`(差分描画用フレームバッファ), `Preview`(`PreviewPane`), `Search`(`SearchState`), `Leap`(`LeapState`)を保持する。
- **`static class Program`**: `List<Column> Columns` と `ScreenState State` を **static グローバルとして1セットだけ**保持する。`MoveUpAsync`, `RebuildRightSideAsync` などのナビゲーション関数群は、引数を取らずにこれらの static フィールドを暗黙に参照して動作する。
- **描画**: `Draw()` が `BuildFrame(width, height)` を呼び、画面全体を表す `Line[]` を構築したうえで文字列配列 `frame[]` にレンダリングする。`BuildFrame` は各カラムを `DrawColumnToLines` で `displayX` オフセット付きに描画し、プレビューペインを右側に追加し、最後にフルパス行(`frame[height - 2]`)とステータス行(`frame[height - 1]`)を上書きする。`PrevFrame` との行単位差分だけを実際の端末に書き出すことで再描画コストを抑えている。
- **CJK 幅処理**: `CharacterWidth` クラス(`GetStringWidth` / `PadToWidth` / `SmartTruncate`)が全角文字幅を計算し、パディングや省略を行う。
- **入力**: `Main` のメインループが `Console.ReadKey(true)` でキー入力を取得する。分岐の優先順位は `State.Leap.Active` → `State.Search.Active` → 通常キー処理(`HandleSpecialKeyAsync` 等)であり、「モード中は入力を先取りする」というパターンがすでに確立している(`HandleLeapKeyAsync`, `HandleSearchKeyAsync`)。
- **エラー表示**: `_lastErrorMessage`(static な `string?`)にメッセージをセットすると、次回の `BuildFrame` でステータス行に1度だけ表示され、表示後にクリアされる。
- **共有機能**: カーソルメモリ(ディレクトリごとの選択位置記憶)と migemo(`MigemoProvider` によるローマ字→日本語検索)は、カラム集合全体で共有される機能であり、ペインが増えても分割してはならない。
- **テスト**: `Zurari.Tests` に xUnit スタイルのユニットテストがある(`NavigationTests`, `SearchMatchTests` 等)。

この構造に対して、ファイルのコピー/移動機能を追加する。移動元と移動先を同時に見せるには画面の上下分割が要り、衝突時の確認や宛先選択には軽量なダイアログ基盤が要る。したがって、コピー/移動そのものより前に、この2つの土台を整備する必要がある。

---

## 目的

ファイルのコピー/移動機能を追加する。実現のために以下を先行して整備する。

1. 上下2ペイン分割(トグル式) — コピー/移動元と移動先を同時に表示するため。
2. 軽量ダイアログ基盤(確認 / 1行テキスト入力 / リスト選択) — 削除確認、リネーム入力、衝突解決、宛先選択などに共通して使う部品として。

---

## 本来の問いと回答

機能追加を「2ペイン描画」と「コピー処理」の実装として捉えると壊れる。本質的には次の5つの問いに答える必要がある。

### 1. これは画面分割の問題ではなく状態の問題である

現状、ナビゲーション状態(`Columns` + `ActiveColumn` / `HorizontalScroll` / `Search` / `Leap` / `Preview`)は static な1セットしか存在しない。2ペイン化とは「この状態セットを2つ持つ」ことであり、「画面を2分割して描画する」ことは派生的な結果に過ぎない。

**回答**: これらを `Pane` クラスに束ね、インスタンス化可能にする。既存のナビゲーション関数はすべて `Pane` を引数として受け取るシグネチャに変更する。一方で、カーソルメモリ・migemo・エラーメッセージ(`_lastErrorMessage`)は引き続きプロセス全体で共有する static のままとする。ペインごとに分離するのは「今どこを見ているか」の状態であり、「アプリ全体の設定・記憶・通知」ではない。

### 2. 描画はサブ矩形に対応できるか

現状の `BuildFrame` は画面全体の高さを前提に、カラム描画・プレビュー描画・パス行・ステータス行を1つの `Line[]` に直接書き込んでいる。2ペインになると、各ペインは画面の上半分・下半分という「サブ矩形」の中でしか描画できない。

**回答**: 各ペインの描画関数にビューポート(`yOffset`, `height`)を渡せるようにする。合成は次の3層で行う。

1. **各ペインの矩形描画** — 各ペインが自分の `yOffset`〜`yOffset+height` の範囲にカラム・プレビューを描画する。
2. **共有フッター** — パス行とステータス行を画面最下部2行に上書きする(全ペイン共通で1組のみ)。
3. **オーバーレイ** — ダイアログが開いていれば最後に中央ボックスを上書きする。

`PrevFrame` による行単位の差分描画は、この3層合成が終わった最終フレームに対してそのまま適用する。合成のどの層で行が変わったかに関わらず、差分検出ロジック自体は変更しない。

### 3. キーボードは誰のものか

2ペイン+ダイアログという複数の「入力を欲しがる主体」が生まれるが、キー入力の経路は1つしかない。

**回答**: 優先順位を「ダイアログ > フォーカスペインの Leap/Search > フォーカスペインの通常キー」の1系統に固定し、1箇所でルーティングする。ダイアログは Leap/Search と同族の「アクティブなら入力を独占するモード」であるため、新しい入力モデルを発明する必要はなく、既存パターン(`State.Leap.Active` → `State.Search.Active` → 通常キー)の一般化で足りる。非フォーカスペインは常にキー入力を受け取らない。

### 4. ダイアログは別画面かオーバーレイか

**回答**: オーバーレイとする。別画面(画面クリア＋専用ループ)にはしない。`BuildFrame` の最終段階で `Line[]` の該当範囲に中央ボックスを上書き描画する方式にする。これにより差分描画の仕組みをそのまま流用でき、背後のペイン内容が破棄されない。CJK 幅計算は既存の `CharacterWidth` をそのまま再利用する。

部品は次の3つに限定する。

- 確認ダイアログ(Y/N/Esc)
- 1行テキスト入力ダイアログ
- リスト選択ダイアログ(インクリメンタル絞り込み付き)

最大のリスクは IME 経由の日本語入力である。`Console.ReadKey` は IME 変換中の入力やサロゲートペアの扱いに癖があり、実機でしか確認できない。この経路は新規に作らず、既存の検索入力処理(`HandleSearchKeyAsync` が使っている入力経路)を再利用し、Phase 3 で実機検証を行う。

### 5. コピー/移動は UI の問題ではなくエンジンの問題である

コピー/移動を「キー入力を受けてファイルをコピーする処理」としてダイアログ層に直接書くと、進捗表示・キャンセル・衝突処理が UI コードに絡み合う。

**回答**: 進捗通知(`IProgress<T>`)・キャンセル(`CancellationToken`)・衝突時コールバックを持つ非同期エンジンを UI から分離する。UI 層はエンジンを呼び出し、進捗をステータス行に反映するだけにする。衝突解決(上書き/スキップ/リネーム)の選択肢提示にはリスト選択ダイアログを使うため、ダイアログ基盤の存在がコピー/移動エンジンの前提条件になる。

---

## 設計

### 状態設計

`Pane` クラスを新設し、現在 `ScreenState` および static グローバルに散在している「1画面分のナビゲーション状態」をまとめる。

```csharp
sealed class Pane
{
    public List<Column> Columns = new();
    public int ActiveColumn;
    public int HorizontalScroll;
    public SearchState Search = new();
    public LeapState Leap = new();
    public PreviewPane Preview = new();
}
```

`ScreenState` は次の役割のみに縮小する。

```csharp
sealed class ScreenState
{
    public string[]? PrevFrame;
    public int PrevWidth;
    public int PrevHeight;
    public List<Pane> Panes = new();   // 常に1〜2要素
    public int FocusedPane;            // 0 = 上(または単一)ペイン, 1 = 下ペイン
    public bool SplitActive;           // トグル状態
    public DialogState? Dialog;        // 開いているダイアログ(null = なし)
}
```

- 非分割時は `Panes.Count == 1`、`FocusedPane == 0` に固定。
- カーソルメモリ・migemo(`_migemo`)・`_lastErrorMessage` は従来通り static のまま、`Pane` の外側で共有する。
- 既存のナビゲーション関数(`MoveUpAsync`, `MoveDownAsync`, `RebuildRightSideAsync`, `EnterAsync` 等)は、シグネチャの先頭引数に `Pane pane` を追加する形にリファクタリングする。呼び出し側は `Panes[State.FocusedPane]` を渡す。

### 描画設計

`BuildFrame(width, height)` を3層構成に分解する。

1. **ペイン矩形描画**
   - `SplitActive == false`: `Panes[0]` を `yOffset = 0`, `height = height - 2`(フッター2行を除く)でフル描画。
   - `SplitActive == true`: 画面の高さを2分割する。上ペイン `yOffset = 0`, 下ペイン `yOffset = paneHeight`。境界に1行の区切り線を挟む(`paneHeight = (height - 2 - 1) / 2` を基準に上下へ割り振り、端数は下ペインに寄せる)。
   - 各ペインの描画関数(`DrawColumnToLines`, `DrawPreviewToLines` を呼ぶラッパー、たとえば `DrawPaneToLines(Line[] lines, Pane pane, int yOffset, int height, bool focused, int width)`)は、`yOffset` を全書き込み行インデックスに加算するだけで既存の描画ロジックをそのまま使う。
   - **非フォーカスペインの減光**: `focused == false` のとき、通常カラーパレットの代わりに「減光パレット」を使う。ディレクトリ色(通常 ANSI 94)はグレー系 ANSI 90 に差し替える。同様に、選択中カーソルの強調色・その他のテキスト色もそれぞれのグレー系相当に差し替える。SGR 2(dim)は端末実装によって効果が不安定なため使用しない。差し替えは色コード文字列を選ぶテーブルまたは分岐によって行い、`DrawColumnToLines` 系の呼び出し時に「どちらのパレットを使うか」を bool 引数として渡す。
   - フォーカスペインの枠・カーソル表示は従来通り。非フォーカスペインは枠・カーソルの強調表示も行わない(通常テキストと同等の弱い表示)。

2. **共有フッター**
   - パス行(`frame[height - 2]`)は **フォーカスペインの** `GetCurrentFullPath()` 相当の値を表示する。
   - 2ペイン分割時のみ、パス行の先頭にどちらのペインかを示すプレフィックスを付与する。`[^]`(上ペインがフォーカス)または `[v]`(下ペインがフォーカス)。単一ペイン時はプレフィックスを付けない。
   - ステータス行(`frame[height - 1]`)は Leap/Search/エラー/通常ヒントの優先順位のロジックをそのまま維持するが、参照先を `Panes[State.FocusedPane].Leap` / `.Search` に変える。

3. **オーバーレイ**
   - `State.Dialog != null` の場合、最後に `Line[]` 上へ中央ボックスを上書きする(詳細は「ダイアログ設計」節)。

`PrevFrame` との行単位差分描画ロジック自体は変更しない。3層合成が終わった `frame[]` を渡すだけで、差分検出は既存コードのままで良い。

単一ペイン・ダイアログなし状態では、旧 `BuildFrame` と全く同じ出力になることを Phase 2 の完了条件とする。

### 入力ルーティング設計

メインループの入力分岐を次の優先順位1本に統合する。

```
if (State.Dialog != null)                         → HandleDialogKeyAsync(key)
else if (FocusedPane.Leap.Active)                  → HandleLeapKeyAsync(FocusedPane, key)
else if (FocusedPane.Search.Active)                → HandleSearchKeyAsync(FocusedPane, key)
else                                                → HandleSpecialKeyAsync(FocusedPane, key)   // 通常キー
```

- ダイアログが開いている間は、フォーカスペインの Leap/Search 状態がアクティブであっても入力は届かない(ダイアログが最優先)。
- `Ctrl+W`(分割トグル)と `Tab`(フォーカス移動)は、ダイアログが開いていない場合に限り通常キー処理の一部として扱う。
- 非フォーカスペインは常に入力を受け取らない。ペイン間で状態が混線することはない。

### ダイアログ設計

`DialogState` を新設し、3種類の部品を1つの状態機械として表現する。

```csharp
abstract class DialogState
{
    public string Title = "";
}

sealed class ConfirmDialogState : DialogState
{
    public string Message = "";
    // Y/N/Esc のみ。結果は呼び出し元に Action<bool?> で通知(true=Yes, false=No, null=Esc)
}

sealed class TextInputDialogState : DialogState
{
    public string Buffer = "";
    public int CursorPos;
    // 例: リネームの初期値としてファイル名を Buffer にプリセット
}

sealed class ListSelectDialogState : DialogState
{
    public List<string> AllItems = new();
    public List<string> FilteredItems = new();
    public string FilterQuery = "";
    public int Selected;
    // インクリメンタル絞り込み: 入力ごとに AllItems を部分文字列一致でフィルタ
}
```

- 表示位置は画面中央固定。幅・高さは内容(タイトル・メッセージ長・リスト件数)に応じて自動計算する。
- 描画は `BuildFrame` 最終段階で `Line[]` の該当矩形に直接書き込む。文字幅計算は `CharacterWidth.GetStringWidth` / `PadToWidth` / `SmartTruncate` を再利用する。
- 入力処理(`HandleDialogKeyAsync`)は種類ごとに分岐する。
  - `ConfirmDialogState`: `Y`/`Enter` → true, `N` → false, `Esc` → null。結果をコールバックへ渡してダイアログを閉じる。
  - `TextInputDialogState`: 印字可能文字はカーソル位置に挿入、`Backspace`/`Delete`/矢印キーで編集、`Enter` で確定、`Esc` でキャンセル。**日本語入力(サロゲートペア・IME 確定文字列)を含む文字受け取りの経路は、既存の `HandleSearchKeyAsync` の入力処理をそのまま再利用する**(新規の入力処理は書かない)。
  - `ListSelectDialogState`: 印字可能文字で `FilterQuery` に追加しインクリメンタル絞り込み、`↑`/`↓` で選択移動、`Enter` で確定、`Esc` でキャンセル。
- ダイアログのライフサイクルは「開く関数(タイトル・初期値・完了時コールバックを受け取る)」→「`State.Dialog` にセット」→「`HandleDialogKeyAsync` が確定/キャンセルで `State.Dialog = null` に戻し、コールバックを呼ぶ」という一方向の流れに統一する。

### コピー/移動エンジン設計

UI から独立した非同期エンジンとして実装する。

```csharp
enum ConflictResolution { Overwrite, Skip, Rename }

interface IFileOpProgress
{
    void Report(FileOpProgressInfo info); // 現在処理中のパス、完了件数/総件数、転送バイト数など
}

static class FileOpEngine
{
    // sources: 複数件対応。Phase 6 の呼び出しは1件のみ渡すが、シグネチャは将来のマーク機能に備え複数件を受け付ける。
    static Task CopyAsync(
        IReadOnlyList<string> sources,
        string destDir,
        IFileOpProgress progress,
        Func<string, Task<ConflictResolution>> onConflict,
        CancellationToken ct);

    static Task MoveAsync(
        IReadOnlyList<string> sources,
        string destDir,
        IFileOpProgress progress,
        Func<string, Task<ConflictResolution>> onConflict,
        CancellationToken ct);
}
```

- 衝突(宛先に同名ファイル/ディレクトリが存在)時は `onConflict` コールバックを呼び、呼び出し元(UI 層)がリスト選択ダイアログ(上書き/スキップ/リネーム)を表示して結果を返す。エンジン自体はダイアログを知らない。
- 進捗は `IFileOpProgress.Report` を通じてステータス行に反映する。UI 層は進捗情報を受け取り次第、次の `Draw()` でステータス行に「コピー中: N/M件 (現在のファイル名)」のように表示する。
- キャンセルは `CancellationToken` で伝播する。UI 層は `Esc` 押下などをトリガーにキャンセルを要求する。
- ファイル操作失敗(権限・ロック・パス長など)は例外を捕捉し、`_lastErrorMessage` 経由でステータス行に表示する(新しいエラー表示経路は作らない)。
- ディレクトリのコピー/移動は再帰的に行い、進捗はファイル単位で報告する。

---

## 実装フェーズ

各フェーズ完了時点で、アプリは常に動作する状態を保つ。

- **Phase 1: Pane 抽出リファクタ**
  `Column` の集合と `ActiveColumn` / `HorizontalScroll` / `Search` / `Leap` / `Preview` を `Pane` クラスにまとめ、ナビゲーション関数を `Pane` 引数を取る形にリファクタリングする。`ScreenState` にはフレームバッファ(`PrevFrame` 等)とフォーカス中ペインの参照のみを残す。見た目は変更しない。
  **完了条件**: 既存のユニットテスト(`Zurari.Tests`)がすべて緑のまま。

- **Phase 2: ビューポート描画**
  `BuildFrame` を「ペイン矩形描画 → 共有フッター → オーバーレイ」の3層合成に分解する。見た目は変更しない。
  **完了条件**: 単一ペイン・ダイアログなしの状態で、Phase 1 以前と画面出力(端末に書き込まれるバイト列)が同一であることを確認する。

- **Phase 3: 入力ルーティング一般化 + ダイアログ3部品**
  「ダイアログ > フォーカスペインの Leap/Search > フォーカスペインの通常キー」の優先順位に統合し、確認/1行テキスト入力/リスト選択の3ダイアログを実装する。日本語 IME 入力の実機検証を含む。
  **完了条件**: 3種のダイアログが単独で開閉・確定・キャンセルでき、日本語入力を含むテキスト入力ダイアログが実機で文字化けなく動作する。

- **Phase 4: ダイアログ実証機能**
  単一画面のまま価値が出る機能をダイアログ基盤の上に実装する。
  - 削除(`d` または `Delete`): 確認ダイアログを表示し、Windows ではごみ箱送りを既定動作とする。
  - リネーム(`r`): テキスト入力ダイアログ、初期値は現在のファイル名。
  - ブックマーク(`b` = 一覧からジャンプ、`Shift+B` = 現在ディレクトリを追加): `%APPDATA%\zurari\bookmarks.txt` に永続化する。
  - ヒストリジャンプ(`h`): セッション内の訪問履歴からリスト選択ダイアログで選ぶ。
  **完了条件**: 削除・リネーム・ブックマーク・ヒストリジャンプの4機能がすべて実機で動作し、ユニットテストが緑。

- **Phase 5: 上下2ペイン(トグル式)**
  `Ctrl+W` で分割の開閉、`Tab` でフォーカス移動を実装する。各ペインは独立した Miller カラム群と検索/Leap 状態を持つ。第2ペイン(下ペイン)を開いたときの初期パスは、その時点でフォーカスしていたペインの現在ディレクトリとする。プレビューは各ペイン内で従来ルール通りに動作する。
  **完了条件**: `Ctrl+W` で分割の開閉が往復でき、`Tab` でフォーカスが移動し、両ペインが独立してナビゲーション・検索・Leap を実行できる。非フォーカスペインの減光表示とパス行の `[^]`/`[v]` プレフィックスが正しく切り替わる。

- **Phase 6: コピー/移動**
  コピーは `c` キー、移動は `m` キーで実行する。コピー先/移動先は非フォーカスペインのアクティブカラムのディレクトリとする。対象はカーソル下の1件のみとする(エンジンのシグネチャ自体は複数件に対応する)。実行中はステータス行に進捗を表示する。分割していない(非分割)状態で `c`/`m` を押した場合は、宛先をブックマークまたはヒストリから選ぶフォールバックダイアログを表示する(Phase 4 で作った部品をそのまま再利用する)。
  **完了条件**: 2ペイン分割時に `c`/`m` でコピー/移動が完了し進捗が表示される。非分割時のフォールバック(ブックマーク/ヒストリからの宛先選択)も動作する。衝突時のリスト選択(上書き/スキップ/リネーム)が機能する。

---

## キー割当一覧表

| キー | モード | 動作 |
|---|---|---|
| `Ctrl+W` | 通常 | 上下2ペイン分割のトグル(開/閉) |
| `Tab` | 通常(分割時) | フォーカスペインを上下で切り替え |
| `c` | 通常 | コピー。分割時: 非フォーカスペインのアクティブカラムのディレクトリへコピー。非分割時: ブックマーク/ヒストリから宛先を選ぶダイアログを表示 |
| `m` | 通常 | 移動。動作は `c` と同様(コピーの代わりに移動) |
| `d` / `Delete` | 通常 | 削除(確認ダイアログ → Windows ではごみ箱送り) |
| `r` | 通常 | リネーム(テキスト入力ダイアログ、初期値=現ファイル名) |
| `b` | 通常 | ブックマーク一覧からジャンプ(リスト選択ダイアログ) |
| `Shift+B` | 通常 | 現在のディレクトリをブックマークに追加 |
| `h` | 通常 | セッション内ヒストリからジャンプ(リスト選択ダイアログ) |
| `F3` | 通常 | Leap モード開始(既存) |
| `/` | 通常 | Search モード開始(既存) |
| `Y` / `Enter` | 確認ダイアログ | Yes |
| `N` | 確認ダイアログ | No |
| `Esc` | 確認/テキスト入力/リスト選択ダイアログ | キャンセルしてダイアログを閉じる |
| 印字可能文字 | テキスト入力/リスト選択ダイアログ | バッファ/絞り込みクエリへ追加 |
| `↑` / `↓` | リスト選択ダイアログ | 選択項目の移動 |
| `Enter` | テキスト入力/リスト選択ダイアログ | 確定 |
| `Backspace` / `Delete` / `←` / `→` | テキスト入力ダイアログ | カーソル移動・編集(既存の検索入力処理を再利用) |

平文字キー(`c` / `m` / `d` / `r` / `b` / `h`)は `HandleSpecialKeyAsync` で現在未割当であることを確認済み。zurari は文字入力で検索が始まる仕様ではない(検索開始は `/` のみ)ため、平文字への機能割当は安全である。

Leap モード・Search モード内のキー割当は既存仕様(`2026-06-22-leap-navigation-design.md`, `2026-06-14-quick-search-design.md`)から変更しない。ただしこれらは「フォーカスペインの」Leap/Search として動作する。

---

## エラー処理とテスト方針

- ファイル操作失敗(権限不足・ファイルロック・パス長超過など)は、既存の `_lastErrorMessage` 経路でステータス行に表示する。新しいエラー表示経路は作らない。
- コピー/移動エンジンは例外を握りつぶさず、呼び出し元へ伝播させたうえで UI 層が `_lastErrorMessage` にセットする。
- ユニットテスト対象は以下の3点とする。
  - ダイアログのレイアウト計算(幅・高さ・中央配置座標が入力サイズに応じて正しく算出されること)。
  - テキスト入力バッファの CJK 幅処理(全角文字混在時のカーソル位置・表示幅計算が `CharacterWidth` の既存ロジックと整合すること)。
  - コピーエンジン(テンポラリディレクトリに実ファイルを作成し、コピー/移動/衝突解決/キャンセルの各シナリオを検証する)。
- 描画結果の見た目(減光パレットの配色、オーバーレイの位置合わせ)および IME 入力は手動確認とする。自動テストの対象にしない。

---

## スコープ外・今後の検討

- **複数マーク(選択)機能はスコープ外**。今回のコピー/移動はカーソル下の1件のみを対象とする。複数マークを実装する場合、`Column.Entries` が単純な `List<string>` であるため、マーク状態(どのエントリがマークされているか)をどこにどう持たせるかは別途設計が必要になる。候補としては `Column` に `HashSet<int> MarkedIndices` を追加する案があるが、ディレクトリ移動時のインデックス整合性、ソート順変更時の対応、ペイン間でのマーク共有可否など未検討の論点が残るため、今回のスペックには含めない。
- コピー/移動の進捗をキャンセル後に途中状態からロールバックする機能は対象外(キャンセル時点までの操作は確定させる)。
- 3ペイン以上への拡張は対象外(トグル式の上下2ペインのみ)。
- ダイアログの位置・サイズをユーザーがカスタマイズする機能は対象外(自動計算・中央固定のみ)。
