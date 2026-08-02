# Phase 8: ジョブ運用 UX 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** バックグラウンドジョブの一覧・キャンセル・busy 警告・操作ログ参照・実行中の影響カラム再読込を実装し、ノンモーダルなコピー/移動の運用面を完成させる。
**Architecture:** Phase 7 で導入済みの `JobManager`/`BackgroundJob`/`OperationLog`/ダイアログ基盤(`ShowConfirm`/`ShowListSelect`)の上に、新しいダイアログ種別 `JobListDialogState` と、busy 判定ヘルパ `PathsOverlap`、メインループの 100ms/500ms 間引きポーリング拡張を追加する。UI 層のみの変更であり、`FileOpEngine`/`JobManager` のコアロジックには手を入れない。
**Tech Stack:** C# / .NET 8, xUnit(Zurari.Tests), 自前 TUI(外部 TUI フレームワーク禁止)

---

## 前提条件(開始前に検証)

- [ ] `dotnet build Zurari.csproj` が exit 0 で成功すること。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` が `Passed!` で全緑になること(Phase 7 で追加された `JobManager`/`FileOpEngine` のテストを含む)。
- [ ] `Program.cs` に `JobManager`, `BackgroundJob`, `JobStatus`, `OperationLog`, `OpLogEntry`, `RecycleBin`, `ShowConfirm`, `ShowListSelect`, `ShowTextInput`, `DialogState`, `ConfirmDialogState`, `ListSelectDialogState`, `RequestConflictAsync` が存在すること(`grep -n "class JobManager\|class OperationLog\|static void ShowConfirm\|static void ShowListSelect" Program.cs` で全て見つかること)。
- [ ] `HandleSpecialKeyAsync(Pane pane, ConsoleKeyInfo key)` に `case 'c':` / `case 'm':` / `case 'd':` / `case ConsoleKey.Delete:` / `case 'r':` が実装済みで、Phase 7 の完了条件(コピー/移動がノンモーダルで動作し、進捗がステータス行に表示される)を手動確認できること。
- [ ] `Pane` クラスに `NeedsRefresh` フィールドが存在すること(Phase 7 で使用済みのはず。`grep -n "NeedsRefresh" Program.cs`)。

この計画と spec に書かれていない判断が必要になったら、自分で決めずに「未解決事項」に記録してそのステップを保留し、次の独立したタスクに進むか停止すること。

---

## 既存コードの前提(Phase 7 完了後の Program.cs、本計画が参照する契約)

以下は Phase 7 で導入済みという前提でこの計画が参照するシグネチャである(spec 「API 契約」節および spec 本文より抜粋。実体は Phase 7 の計画・実装を参照し、本 Phase では変更しない)。

```csharp
enum JobStatus { Pending, Running, Completed, Failed, Cancelled }

sealed class BackgroundJob
{
    public int ShortId; public string OpName = "";           // "コピー" / "移動"
    public JobStatus Status;
    public int DoneCount; public int TotalCount; public long BytesTransferred; public string CurrentFile = "";
    public CancellationTokenSource Cts = new();
    public List<string> SourcePaths = new(); public string DestDir = "";
    public FileOpResult? Result; public DateTime StartTime; public DateTime? EndTime;
}

static class JobManager
{
    public static List<BackgroundJob> Snapshot();            // lock 下のコピー。完了系は直近20件保持
    public static void Cancel(int shortId);
    public static List<string> GetBusyPaths();               // アクティブジョブの SourcePaths + DestDir
    public static bool HasActiveJobs { get; }
}

sealed record OpLogEntry(DateTime Timestamp, int? JobId, string Op, string Result, string Source, string Dest, string Message);
static class OperationLog
{
    public static IReadOnlyList<OpLogEntry> Snapshot();      // 新しい順
}

abstract class DialogState { public string Title = ""; }
sealed class ConfirmDialogState : DialogState { public string Message = ""; public Action<bool?> OnResult = _ => { }; }
sealed class ListSelectDialogState : DialogState { public List<string> AllItems = new(); public List<string> FilteredItems = new(); public string FilterQuery = ""; public int Selected; public Action<string?> OnResult = _ => { }; }

static void ShowConfirm(string title, string message, Action<bool?> onResult);
static void ShowListSelect(string title, IReadOnlyList<string> items, Action<string?> onResult);
```

本 Phase で新規に追加するのは次の1点のみである(spec 「ダイアログ設計」節、ガイド「Phase 8 で導入」節)。

```csharp
sealed class JobListDialogState : DialogState { public List<BackgroundJob> Snapshot = new(); public int Selected; }
```

---

### Task 1: PathsOverlap ヘルパと busy 判定ユニットテスト

**Files:**
- Modify: `Program.cs`(`JobManager` クラスの直後、または近傍の static ヘルパ群に追加)
- Test (Create): `Zurari.Tests/PathsOverlapTests.cs`

- [ ] `Program.cs` 内、`static class JobManager` の直後に `PathsOverlap` を実装する。twf の `FileController.CheckIfBusy`(完全一致のみ)より厳密な「前方一致(ディレクトリ配下を含む)」判定にする必要があるため、以下の通り実装する。

```csharp
static class PathOverlapHelper
{
    // a と b が同一パス、または一方が他方のディレクトリ配下であれば true。
    // 前方一致は「セパレータ境界」でのみ成立させる(C:\a と C:\ab を誤って重複と判定しない)。
    public static bool PathsOverlap(string a, string b)
    {
        string na = NormalizeForOverlap(a);
        string nb = NormalizeForOverlap(b);

        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase))
            return true;

        string naWithSep = na.EndsWith("\\", StringComparison.Ordinal) ? na : na + "\\";
        string nbWithSep = nb.EndsWith("\\", StringComparison.Ordinal) ? nb : nb + "\\";

        if (nb.StartsWith(naWithSep, StringComparison.OrdinalIgnoreCase))
            return true;
        if (na.StartsWith(nbWithSep, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string NormalizeForOverlap(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        return full.TrimEnd('\\', '/');
    }
}
```

- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] `Zurari.Tests/PathsOverlapTests.cs` を新規作成し、次の内容を書く(spec 「busy 判定はパスの前方一致」+ ガイド指定の境界ケースを網羅)。

```csharp
using System.IO;
using Xunit;

namespace Zurari.Tests;

public class PathsOverlapTests
{
    private static string Root => Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\";

    [Fact]
    public void SamePath_Overlaps()
    {
        string p = Path.Combine(Root, "a");
        Assert.True(PathOverlapHelper.PathsOverlap(p, p));
    }

    [Fact]
    public void ChildPath_Overlaps()
    {
        string parent = Path.Combine(Root, "a");
        string child = Path.Combine(Root, "a", "b");
        Assert.True(PathOverlapHelper.PathsOverlap(parent, child));
        Assert.True(PathOverlapHelper.PathsOverlap(child, parent));
    }

    [Fact]
    public void SiblingWithSharedPrefix_DoesNotOverlap()
    {
        // C:\a と C:\ab は前方一致に見えるが、セパレータ境界がないため非重複でなければならない
        string a = Path.Combine(Root, "a");
        string ab = Path.Combine(Root, "ab");
        Assert.False(PathOverlapHelper.PathsOverlap(a, ab));
        Assert.False(PathOverlapHelper.PathsOverlap(ab, a));
    }

    [Fact]
    public void DeepChildPath_Overlaps()
    {
        string parent = Path.Combine(Root, "a");
        string deepChild = Path.Combine(Root, "a", "b", "c", "d.txt");
        Assert.True(PathOverlapHelper.PathsOverlap(parent, deepChild));
    }

    [Fact]
    public void UnrelatedPaths_DoNotOverlap()
    {
        string a = Path.Combine(Root, "a");
        string b = Path.Combine(Root, "b");
        Assert.False(PathOverlapHelper.PathsOverlap(a, b));
    }

    [Fact]
    public void CaseInsensitive_Overlaps()
    {
        string lower = Path.Combine(Root, "a", "b");
        string upperParent = Path.Combine(Root, "A");
        Assert.True(PathOverlapHelper.PathsOverlap(lower, upperParent));
    }

    [Fact]
    public void TrailingSeparator_SamePath_Overlaps()
    {
        string p1 = Path.Combine(Root, "a");
        string p2 = Path.Combine(Root, "a") + Path.DirectorySeparatorChar;
        Assert.True(PathOverlapHelper.PathsOverlap(p1, p2));
    }
}
```

- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、`PathsOverlapTests` の6件がすべて成功することを確認する(`Passed!`)。
- [ ] コミットする。

```
git add Program.cs Zurari.Tests/PathsOverlapTests.cs
git commit -m "feat: add PathOverlapHelper.PathsOverlap for busy path detection"
```

---

### Task 2: busy 警告 — c/m/d/r の実行直前フック

**Files:**
- Modify: `Program.cs`(`HandleSpecialKeyAsync` 内、`case 'c':` / `case 'm':` / `case 'd':` / `case ConsoleKey.Delete:` / `case 'r':` の各ハンドラ)

前提: Phase 7 の実装で、`c`/`m`/`d`/`r` の各ハンドラは「対象パスリストを組み立てる」→「ジョブ登録 or 同期実行」という流れになっている(複数マーク設計によりコピー/移動/削除は対象パスのリスト、リネームは単一パス)。本タスクはこの「対象パスリストが確定した直後・実行に入る前」に busy チェックを差し込む。

- [ ] `Program.cs` に、対象パスリストと busy 判定を1箇所にまとめる共通ヘルパを追加する。`PathOverlapHelper` の直後に置く。

```csharp
static class BusyWarningHelper
{
    // targetPaths のいずれかが実行中ジョブの busy パスと重なれば true(= 警告が必要)。
    public static bool AnyOverlapsBusyPaths(IReadOnlyList<string> targetPaths)
    {
        List<string> busyPaths = JobManager.GetBusyPaths();
        if (busyPaths.Count == 0) return false;

        foreach (string target in targetPaths)
        {
            foreach (string busy in busyPaths)
            {
                if (PathOverlapHelper.PathsOverlap(target, busy))
                    return true;
            }
        }
        return false;
    }
}
```

- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] `case 'c':` のハンドラ(コピー実行、Phase 7 で実装済み)を開き、対象パスリスト(`sourcePaths` 相当の変数。Phase 7 実装での実際の変数名を確認して合わせること)が確定した直後、ジョブ登録より前に次の分岐を挿入する。

```csharp
if (BusyWarningHelper.AnyOverlapsBusyPaths(sourcePaths))
{
    ShowConfirm("busy 警告", "実行中のジョブが対象パスを使用しています。続行しますか?", proceed =>
    {
        if (proceed == true)
        {
            StartCopyJob(pane, sourcePaths, destDir); // Phase 7 実装の実ジョブ登録ロジックを呼ぶ既存の内部関数/ローカル関数を呼び出す
        }
    });
    return;
}
StartCopyJob(pane, sourcePaths, destDir);
```

  - 注意: `StartCopyJob` のような分離関数が Phase 7 の実装に存在しない場合(ジョブ登録処理がインライン展開されている場合)は、そのインライン処理をローカル関数として切り出してから上記の分岐に組み込むこと。切り出し方法に選択の余地がある場合は、既存コードの変数キャプチャが壊れない最小限の切り出しを選ぶ。
- [ ] `case 'm':`(移動)にも同様の分岐を追加する。busy 判定対象パスと確認メッセージは `c` と同一の文言・構造を使う。
- [ ] `case 'd':` / `case ConsoleKey.Delete:`(削除)にも同様の分岐を追加する。削除確認ダイアログ(Phase 4 で導入済み)より**前**に busy チェックを行い、busy 警告 → Yes の場合のみ既存の削除確認ダイアログを表示する(2段階の確認になる。busy 警告 → 削除確認、の順)。
- [ ] `case 'r':`(リネーム)にも同様の分岐を追加する。対象パスはカーソル下の1件のみ(spec よりリネームはマーク非対応)。
- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] 手動確認(手順は本ファイル末尾の「手動確認手順」節を参照)を実施する。
- [ ] コミットする。

```
git add Program.cs
git commit -m "feat: warn before c/m/d/r when target paths overlap active job busy paths"
```

---

### Task 3: JobListDialogState と Shift+J ジョブ一覧ダイアログ

**Files:**
- Modify: `Program.cs`(`DialogState` 系クラス定義の近傍に `JobListDialogState` 追加、`HandleSpecialKeyAsync` に `Shift+J` 分岐追加、`HandleDialogKeyAsync` に `JobListDialogState` 分岐追加、`BuildFrame` のオーバーレイ層描画に対応追加、メインループのポーリング部に 100ms 更新追加)

- [ ] `Program.cs` の `ListSelectDialogState` 定義の直後に `JobListDialogState` を追加する。

```csharp
sealed class JobListDialogState : DialogState
{
    public List<BackgroundJob> Snapshot = new();
    public int Selected;
}
```

- [ ] ジョブ一覧の行フォーマットを組み立てるヘルパを追加する。`JobListDialogState` 定義の直後に置く。

```csharp
static class JobListFormatter
{
    // 例: "#3 コピー Running 45% (12/40) src -> dst"
    public static string FormatRow(BackgroundJob job, int width)
    {
        string statusText = job.Status switch
        {
            JobStatus.Pending => "Pending",
            JobStatus.Running => "Running",
            JobStatus.Completed => "Completed",
            JobStatus.Failed => "Failed",
            JobStatus.Cancelled => "Cancelled",
            _ => job.Status.ToString(),
        };

        int percent = job.TotalCount > 0 ? (int)(job.DoneCount * 100L / job.TotalCount) : 0;

        string srcLabel = job.SourcePaths.Count == 1
            ? System.IO.Path.GetFileName(job.SourcePaths[0].TrimEnd('\\', '/'))
            : $"{job.SourcePaths.Count}件";
        string dstLabel = System.IO.Path.GetFileName(job.DestDir.TrimEnd('\\', '/'));

        string row = $"#{job.ShortId} {job.OpName} {statusText} {percent}% ({job.DoneCount}/{job.TotalCount}) {srcLabel} -> {dstLabel}";
        return CharacterWidth.SmartTruncate(row, width);
    }
}
```

- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] `Program.cs` の `static void ShowConfirm(...)` / `ShowListSelect(...)` の近傍に、ジョブ一覧を開く関数を追加する。

```csharp
static void ShowJobList()
{
    var dialog = new JobListDialogState
    {
        Title = "ジョブ一覧",
        Snapshot = JobManager.Snapshot(),
        Selected = 0,
    };
    State.Dialog = dialog;
}
```

- [ ] `HandleSpecialKeyAsync(Pane pane, ConsoleKeyInfo key)` 内、既存の `case` 群(`case 'b':` 等が並ぶ箇所)に `Shift+J` の分岐を追加する。zurari のキー判定は `key.KeyChar` ベースで大文字小文字を区別している既存コード(例えば `Shift+B` の判定箇所)と同じスタイルに合わせる。

```csharp
case 'J' when (key.Modifiers & ConsoleModifiers.Shift) != 0 || key.KeyChar == 'J':
    ShowJobList();
    return;
```

  - 注意: 既存の `Shift+B`(`case 'B':` 等)の実装箇所を確認し、そこで使われている修飾キー判定パターンと完全に一致させること。パターンが異なる場合はそちらに合わせる(本計画の擬似コードより既存コードの実パターンを優先する)。
- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] `HandleDialogKeyAsync`(Phase 3 で導入済みのダイアログキー処理の分岐関数)に `JobListDialogState` の分岐を追加する。既存の `ConfirmDialogState` / `TextInputDialogState` / `ListSelectDialogState` の分岐と並列に配置する。

```csharp
else if (State.Dialog is JobListDialogState jobList)
{
    if (key.Key == ConsoleKey.UpArrow)
    {
        if (jobList.Selected > 0) jobList.Selected--;
    }
    else if (key.Key == ConsoleKey.DownArrow)
    {
        if (jobList.Selected < jobList.Snapshot.Count - 1) jobList.Selected++;
    }
    else if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Delete)
    {
        if (jobList.Selected >= 0 && jobList.Selected < jobList.Snapshot.Count)
        {
            BackgroundJob target = jobList.Snapshot[jobList.Selected];
            if (target.Status == JobStatus.Pending || target.Status == JobStatus.Running)
            {
                int shortId = target.ShortId;
                ShowConfirm("ジョブキャンセル", $"#{shortId} {target.OpName} をキャンセルしますか?", confirmed =>
                {
                    if (confirmed == true)
                    {
                        JobManager.Cancel(shortId);
                    }
                    ShowJobList(); // 一覧を最新状態で再表示
                });
            }
        }
    }
    else if (key.Key == ConsoleKey.Escape)
    {
        State.Dialog = null;
    }
}
```

  - 注意: `HandleDialogKeyAsync` の既存分岐が `if (State.Dialog is ConfirmDialogState confirm) { ... } else if (...)` のような形か、あるいは `switch (State.Dialog)` パターンマッチかは Phase 3 の実装を確認してから、既存のスタイルに合わせて追記すること。異なる場合は本計画の擬似コードよりも既存コードの実パターンを優先する。
- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] `BuildFrame` のオーバーレイ層描画箇所(`State.Dialog != null` の分岐)に `JobListDialogState` のケースを追加する。既存の `ListSelectDialogState` の描画ロジック(中央ボックス+スクロール可能なリスト表示)を再利用しつつ、行の生成だけ `JobListFormatter.FormatRow` に差し替える。

```csharp
else if (State.Dialog is JobListDialogState jobListDlg)
{
    int boxWidth = System.Math.Min(width - 4, 80);
    var rowTexts = new List<string>();
    foreach (BackgroundJob job in jobListDlg.Snapshot)
    {
        rowTexts.Add(JobListFormatter.FormatRow(job, boxWidth - 4));
    }
    DrawListBoxOverlay(lines, jobListDlg.Title, rowTexts, jobListDlg.Selected, width, height);
}
```

  - 注意: `DrawListBoxOverlay` に相当する共通描画関数が `ListSelectDialogState` の描画に既に存在するはずである(Phase 3 で実装済み)。その関数名を実際のコードから確認し、シグネチャに合わせて呼び出すこと。関数が存在せず `ListSelectDialogState` 描画がインライン展開されている場合は、そのインラインロジックを流用してジョブ一覧用に複製せず、行リスト+タイトル+選択位置を受け取る形の共通関数として抽出してから両方から呼ぶこと(重複コードを避ける)。
- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] メインループの無入力時ポーリング処理(`Thread.Sleep(50)` を含む `while (true)` ループ内、既存の 100ms 間引き処理があればその近傍。Phase 7 で「連続量の表示更新は100ms間引き」を実装済みのはずなので、そのタイマー変数を流用する)に、ジョブ一覧ダイアログが開いている間のスナップショット更新を追加する。

```csharp
if (State.Dialog is JobListDialogState openJobList
    && (DateTime.UtcNow - _lastJobPollTime).TotalMilliseconds >= 100)
{
    int previousSelected = openJobList.Selected;
    openJobList.Snapshot = JobManager.Snapshot();
    openJobList.Selected = System.Math.Min(previousSelected, System.Math.Max(0, openJobList.Snapshot.Count - 1));
    _lastJobPollTime = DateTime.UtcNow;
}
```

  - `_lastJobPollTime` は Phase 7 で導入済みの 100ms 間引きタイマー変数と共用できるなら共用し、専用に必要な場合のみ `static DateTime _lastJobPollTime = DateTime.MinValue;` をメインループ近傍の static フィールド群に追加する。どちらを取るかは Phase 7 実装の実際のタイマー変数名・スコープを確認してから決定する。共用可否の判断がつかない場合は「未解決事項」に記録して専用タイマーで進める。
- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] 手動確認(本ファイル末尾「手動確認手順」節、手順1・2)を実施する。
- [ ] コミットする。

```
git add Program.cs
git commit -m "feat: add Shift+J job list dialog with live snapshot refresh and cancel"
```

---

### Task 4: Shift+L 操作ログビュー

**Files:**
- Modify: `Program.cs`(`HandleSpecialKeyAsync` に `Shift+L` 分岐追加)

- [ ] `Program.cs` に操作ログのエントリを表示用文字列に変換するヘルパを追加する。`JobListFormatter` の直後に置く。

```csharp
static class OperationLogFormatter
{
    // 例: "2026-07-06 14:32:10 [#3] COPY OK C:\src\a.txt -> C:\dst\a.txt"
    public static string FormatEntry(OpLogEntry entry)
    {
        string jobLabel = entry.JobId.HasValue ? $"[#{entry.JobId.Value}]" : "[-]";
        string line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} {jobLabel} {entry.Op} {entry.Result} {entry.Source} -> {entry.Dest}";
        if (!string.IsNullOrEmpty(entry.Message))
        {
            line += $" {entry.Message}";
        }
        return line;
    }
}
```

- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] `HandleSpecialKeyAsync` に `Shift+L` の分岐を追加する。`Shift+J` と同じ修飾キー判定パターンを使う。

```csharp
case 'L' when (key.Modifiers & ConsoleModifiers.Shift) != 0 || key.KeyChar == 'L':
{
    List<string> logLines = new List<string>();
    foreach (OpLogEntry entry in OperationLog.Snapshot())
    {
        logLines.Add(OperationLogFormatter.FormatEntry(entry));
    }
    ShowListSelect("操作ログ", logLines, _ => { }); // 確定は閉じるのみ。選択結果は使わない
    return;
}
```

- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] 手動確認(本ファイル末尾「手動確認手順」節、手順3)を実施する。migemo 絞り込みが効くことも確認する。
- [ ] コミットする。

```
git add Program.cs
git commit -m "feat: add Shift+L operation log viewer with migemo filtering"
```

---

### Task 5: 実行中の 500ms 間引き影響カラム再読込

**Files:**
- Modify: `Program.cs`(メインループのポーリング処理。Phase 7 で実装済みの「ジョブ完了時の影響カラム再読込」処理の近傍を拡張する)

前提: Phase 7 で「ジョブ完了時に必ず1回、影響カラムを再読込する」処理が実装済みのはずである(spec 「完了後のフィードバックとペイン再読込」節)。その実装は「対象カラム特定 → `Leap`/`Search` アクティブなら `NeedsRefresh` を立てて遅延 → そうでなければ即座に `CachedChildren` 無効化 + `RebuildRightSideAsync` 再実行」という関数(仮に `RefreshAffectedColumnsAsync(IReadOnlyList<string> affectedPaths)` のような形)にまとまっているはずである。本タスクはこれを「ジョブ完了時」だけでなく「実行中も500ms間引きで」呼び出すよう拡張する。

- [ ] Phase 7 実装から、影響カラム再読込の実処理関数の実際の名前とシグネチャを確認する(`grep -n "RefreshAffectedColumns\|CachedChildren = null\|NeedsRefresh = true" Program.cs` 等で特定)。
- [ ] メインループの無入力時ポーリング処理に、実行中ジョブがある場合の 500ms 間引き再読込を追加する。既存の「ジョブ完了検知」処理の近傍に置く。

```csharp
if (JobManager.HasActiveJobs
    && (DateTime.UtcNow - _lastActiveJobColumnRefresh).TotalMilliseconds >= 500)
{
    List<string> activeBusyPaths = JobManager.GetBusyPaths();
    if (activeBusyPaths.Count > 0)
    {
        await RefreshAffectedColumnsAsync(activeBusyPaths); // Phase 7 実装の実関数名に置き換える
    }
    _lastActiveJobColumnRefresh = DateTime.UtcNow;
}
```

  - `_lastActiveJobColumnRefresh` は `static DateTime _lastActiveJobColumnRefresh = DateTime.MinValue;` として、他の間引きタイマーと同じ static フィールド群に追加する。
  - `RefreshAffectedColumnsAsync` は Phase 7 で「Path が宛先ディレクトリまたは各ソースの親ディレクトリと一致(OrdinalIgnoreCase)するカラムを再読込」する実装のはずなので、そのシグネチャ(引数がジョブの `SourcePaths`+`DestDir` の組か、フラットな busy パスのリストか)を確認し、`JobManager.GetBusyPaths()` の戻り値(`SourcePaths` + `DestDir` のフラットなリスト)をそのまま渡せない場合は、Phase 7 実装のシグネチャに合わせて呼び出し方を調整する。シグネチャの不一致が解消できない場合は「未解決事項」に記録する。
  - Leap/Search アクティブなペインを自動再読込しない保護規則(`NeedsRefresh` フラグを立てて遅延)は Phase 7 の実装にすでに組み込まれているはずなので、本タスクでは変更しない(同じ関数を呼ぶだけなので自動的に規則が適用される)。
- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] 手動確認(本ファイル末尾「手動確認手順」節、手順4)を実施する。
- [ ] コミットする。

```
git add Program.cs
git commit -m "feat: refresh affected columns every 500ms while jobs are active"
```

---

### Task 6: 統合検証とリグレッション確認

**Files:** なし(検証のみ)

- [ ] `dotnet build Zurari.csproj` を実行し、exit 0 を確認する。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` を実行し、全テスト(`PathsOverlapTests` を含む)が `Passed!` になることを確認する。
- [ ] 本ファイル末尾「手動確認手順」節の手順1〜5をすべて実施し、期待画面と一致することを確認する。
- [ ] コミットする(このタスクでコード変更がなければコミット不要。手動確認の結果、修正が必要になった場合はその修正を別途コミットする)。

---

## 手動確認手順

### 手順1: ジョブ一覧を開いてキャンセルする

1. 十分な数のファイル(数百件、合計数百MB程度)を含むディレクトリを用意する。
2. 分割モード(`Ctrl+W`)にし、コピー元・コピー先のディレクトリをそれぞれのペインに表示する。
3. コピー元ペインで対象ディレクトリにカーソルを合わせ `c` を押す。ステータス行に「コピー開始: <名前> → <宛先> (#N)」が one-shot 表示されることを確認する。
4. `Shift+J` を押す。画面中央にジョブ一覧ダイアログが開き、`#N コピー Running <進捗>% (<done>/<total>) <src> -> <dst>` の形式の行が表示されることを確認する。
5. 開いたまま数秒待ち、進捗率・件数が自動的に更新されていくことを確認する(ダイアログを閉じずに数値が変わる)。
6. `Enter` を押す。「#N コピー をキャンセルしますか?」の確認ダイアログが表示されることを確認する。
7. `Y` を押す。ジョブがキャンセルされ、一覧の該当行が `Cancelled` になること(または一覧が閉じてステータス行に「キャンセル: N/M件完了」が表示されること)を確認する。
8. `Esc` でジョブ一覧を閉じる。

### 手順2: コピー先カラムでファイルが逐次増える

1. 手順1と同様にコピー元・コピー先を分割ペインで表示する(コピー先は空またはファイル数の少ないディレクトリ)。
2. コピー元ペインで大きめのディレクトリ(数百ファイル)を選び `c` を押す。
3. コピー先ペインにカーソルを移し(`Tab`)、コピー先ディレクトリの中身を見たまま数秒待つ。
4. 500ms 間隔程度でファイル数が増えていく(新しいファイルがカラムに逐次追加表示される)ことを目視確認する。
5. コピー完了後、one-shot ステータスに「コピー完了: N件」が表示され、最終的な件数が実際のファイル数と一致することを確認する。

### 手順3: 操作ログビューと migemo 絞り込み

1. いくつかのファイルをコピー/移動/削除/リネームして操作ログにエントリを蓄積させる(日本語ファイル名を含む操作を最低1件行う)。
2. `Shift+L` を押す。「時刻 [#job] OP RESULT src → dst msg」形式の行が新しい順に並んだリスト選択ダイアログが開くことを確認する。
3. ローマ字で日本語ファイル名の一部(例: 対象が「資料.txt」なら `siryou` または `shiryou`)を入力し、migemo 変換によって該当行に絞り込まれることを確認する。
4. `Esc` でダイアログを閉じ、他の状態(カーソル位置・ペイン内容)に影響がないことを確認する。

### 手順4: busy 警告の発火

1. 大きめのディレクトリのコピーまたは移動を開始し、実行中の状態を作る(`c` または `m`)。
2. ジョブの実行中に、そのソースディレクトリ配下の別ファイル、または宛先ディレクトリ配下のファイルに対して `c`/`m`/`d`/`r` のいずれかを行う。
3. 「実行中のジョブが対象パスを使用しています。続行しますか?」の確認ダイアログが表示されることを確認する。
4. `N` または `Esc` を選ぶと操作が中止されること、`Y` を選ぶと通常通り操作(busy 対象への2つ目の操作)が実行されることを確認する。
5. busy パスと無関係なディレクトリに対して同じキーを押した場合は警告が出ないことを確認する(誤検知がないこと)。
6. 境界確認: busy パスが `C:\data\a` のとき、`C:\data\ab` のような兄弟ディレクトリに対する操作では警告が出ない(前方一致の誤爆がない)ことを確認する。

### 手順5: 削除の2段階確認(busy + 通常)

1. busy 状態のファイルに対して `d` または `Delete` を押す。
2. まず busy 警告ダイアログが表示され、`Y` を選ぶと続けて通常の削除確認ダイアログ(Windows ではごみ箱送りの説明を含む)が表示されることを確認する。
3. 両方で `Y`(または `Enter`)を選ぶと削除が実行され、操作ログに `DELETE` エントリが記録されることを `Shift+L` で確認する。

---

## 完了条件チェックリスト

- [ ] `Shift+J` でジョブ一覧ダイアログが開き、`#N <操作名> <状態> <進捗>% (<done>/<total>) <src> -> <dst>` 形式の行が `CharacterWidth.SmartTruncate` で幅調整されて表示される。
- [ ] ジョブ一覧を開いている間、100ms 間引きでスナップショットが更新され、進捗が生きて見える。
- [ ] ジョブ一覧で `Enter`/`Delete` を押すとキャンセル確認ダイアログが出て、`Y` で `JobManager.Cancel` が呼ばれる。
- [ ] ジョブ一覧で `Esc` を押すとダイアログが閉じるだけで何も実行されない。
- [ ] `Shift+L` で操作ログビューが開き、「時刻 [#job] OP RESULT src → dst msg」形式で新しい順に表示され、migemo を含む絞り込みが効く。確定操作は閉じるのみで副作用がない。
- [ ] `c`/`m`/`d`/`r` の実行直前に `JobManager.GetBusyPaths()` との重なりを判定し、重なれば「実行中のジョブが対象パスを使用しています。続行しますか?」の確認が表示される。
- [ ] `PathOverlapHelper.PathsOverlap` が前方一致(ディレクトリ配下含む)・`OrdinalIgnoreCase` で判定し、`C:\a`/`C:\ab` の非重複、`C:\a`/`C:\a\b` の重複、大文字小文字の同一視をすべて正しく扱う。`PathsOverlapTests.cs` の全ケースが緑。
- [ ] 実行中ジョブがある間、500ms 間引きで影響カラムが再読込され、コピー先を表示中のカラムにファイルが逐次現れる。
- [ ] 対象ペインで Leap/Search がアクティブな間は自動再読込されず、モード終了後に反映される(Phase 7 の保護規則がそのまま適用されている)。
- [ ] `dotnet build Zurari.csproj` が exit 0、`dotnet test Zurari.Tests/Zurari.Tests.csproj` が全緑。
- [ ] 手動確認手順1〜5をすべて実施し、期待画面と一致することを確認済み。

## 未解決事項

- Task 5(実行中の500ms間引き影響カラム再読込)は、実装開始時点で Phase 7 のコードに既に存在していた
  (`RefreshActiveJobColumnsAsync()` + `_lastActiveJobRefreshTime`、メインループから毎ループ呼び出し済み)。
  `JobManager.Snapshot()` から `Status == JobStatus.Running` のジョブのみを対象に 500ms 間引きで
  `RefreshAffectedColumnsAsync(job)`(宛先ディレクトリ+各ソースの親ディレクトリを OrdinalIgnoreCase で
  再読込、Leap/Search アクティブ時は `NeedsRefresh` で遅延)を呼んでおり、本計画が要求する挙動を満たして
  いたため、Task 5 では新規コードを追加していない(判断保留ではなく、確認のみで完了)。
  唯一の差異は対象ジョブを `Running` のみに限定している点(計画の擬似コードは `HasActiveJobs`
  = Pending+Running を対象にしていた)。Pending は semaphore 待ちでまだファイルシステムに触れていない
  ため、Running のみを対象にする現状の実装の方が正しい(無駄な再読込を避けられる)と判断し、そのまま
  維持した。
