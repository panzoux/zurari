# Phase 7: コピー/移動(ジョブ基盤コア) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `FileOpEngine` と `JobManager` を実装し、`c`(コピー)/`m`(移動)キーでノンモーダル(バックグラウンドジョブ)のファイルコピー/移動を実行できるようにする。衝突解決・進捗表示・完了時のペイン再読込・終了時の未完了ジョブ確認までを含む。

**Architecture:** ジョブスレッド(`Task.Run` 内で実行されるコピー/移動処理)は `Pane`/`Column`/`ScreenState` に一切触れない。進捗・状態は `BackgroundJob` の専用フィールドに `lock` 保護で書き込み、読み取り(描画・完了検知・再読込)はすべてメインループが行う「lock + スナップショット」方式(既存の `_searchLock` と同じパターン)。ジョブがユーザー判断(衝突解決)を必要とするときは、ダイアログ要求キューに「ダイアログを開く `Action` + `TaskCompletionSource`」を積み、ジョブスレッドは `await` で待機するだけにする。メインループは `State.Dialog == null` の tick でキューから1件取り出してダイアログを開く。この境界を破って良い例外は無い。

**Tech Stack:** C# / .NET 8, xUnit(Zurari.Tests), 自前 TUI(外部 TUI フレームワーク禁止)

---

## 前提条件(開始前に検証)

- [ ] `git log --oneline -15` で Phase 1〜6 のコミットが存在することを確認する(`Pane` 抽出、ビューポート描画、ダイアログ基盤、削除/リネーム/ブックマーク/ヒストリ、上下2ペイン、複数マークの各 Phase 完了コミット)。
- [ ] `dotnet build Zurari.csproj` → exit code 0。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!`(既存テストがすべて緑)。
- [ ] `Program.cs` を実際に開き、以下がすべて存在することを確認する。存在しない場合は Phase 1〜6 が未完了なので、このタスクを保留し「未解決事項」に記録して停止する。
  - `sealed class Pane`(`Columns`, `ActiveColumn`, `HorizontalScroll`, `Search`, `Leap`, `Preview`, `NeedsRefresh`)
  - `sealed class ScreenState`(`Panes`, `FocusedPane`, `SplitActive`, `Dialog`)
  - `abstract class DialogState` と `ConfirmDialogState` / `TextInputDialogState` / `ListSelectDialogState`、および `ShowConfirm` / `ShowTextInput` / `ShowListSelect`
  - `static class OperationLog`(`Append` / `Snapshot`)、`sealed record OpLogEntry(DateTime Timestamp, int? JobId, string Op, string Result, string Source, string Dest, string Message)`
  - `static class RecycleBin`(`TrySend`)
  - `Column.Marked`(`HashSet<string>`)と、「マークがあれば集合・なければカーソル下1件」の絶対パス一覧を返す関数(Phase 6 導入。正確な関数名を確認してメモしておく — 以下のタスクでは仮に `GetOperationTargets(Pane pane)` と表記するが、実際の名前が異なれば読み替える)
  - ブックマーク+ヒストリの結合済み文字列一覧を返す関数(Phase 4 導入。仮に `BuildBookmarkAndHistoryList()` と表記する)
  - `RebuildRightSideAsync` / `ExitSearchMode` / `ExitLeapModeAsync` が `Pane pane` を先頭引数に取る形にリファクタリング済みであること

この計画と spec に書かれていない判断が必要になったら、自分で決めずに「未解決事項」に記録してそのステップを保留し、次の独立したタスクに進むか停止すること。特に上記の「仮の関数名」が実物と異なる場合は、実物の名前で読み替えて実装し、名前が変わったこと自体は判断保留の対象にしない(これは実装セッションが Program.cs を読めば機械的に確定できるため)。

---

### Task 1: FileOpEngine(コピー/移動エンジン)

**Files:**
- Modify: `C:\Users\user\source\repos\panzoux\zurari\Program.cs`(`sealed class Column` の直前に型・エンジンを追加)
- Create: `C:\Users\user\source\repos\panzoux\zurari\Zurari.Tests\FileOpEngineTests.cs`

**手順:**

- [ ] `Program.cs` の `sealed class Column` の直前に以下の型を追加する。

```csharp
enum ConflictResolution { Overwrite, Skip, Rename }

sealed record ConflictDecision(ConflictResolution Resolution, string? NewName = null);

sealed record FileOpResult(int Succeeded, int Skipped, int Failed, string? FirstError);

sealed record FileOpProgressInfo(int DoneCount, int TotalCount, long BytesTransferred, string CurrentFile);

interface IFileOpProgress
{
    void Report(FileOpProgressInfo info);
    void ReportItemResult(OpLogEntry entry);
}
```

- [ ] 同じ場所(上記の直後)に `FileOpEngine` を追加する。1MB バッファ + `FileOptions.Asynchronous | FileOptions.SequentialScan` の非同期ストリームコピー、衝突はファイルごとのコピー/移動直前に判定、キャンセル・失敗時は宛先の部分ファイルを削除、ディレクトリは再帰、ファイル単位の失敗は記録して継続する。

```csharp
static class FileOpEngine
{
    const int CopyBufferSize = 1024 * 1024; // 1MB
    const FileOptions CopyFileOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;

    enum ItemOutcome { Succeeded, Skipped, Failed, Cancelled }

    public static Task<FileOpResult> CopyAsync(
        IReadOnlyList<string> sources, string destDir, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct)
        => RunBatchAsync(sources, destDir, progress, onConflict, ct, isMove: false);

    public static Task<FileOpResult> MoveAsync(
        IReadOnlyList<string> sources, string destDir, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct)
        => RunBatchAsync(sources, destDir, progress, onConflict, ct, isMove: true);

    static async Task<FileOpResult> RunBatchAsync(
        IReadOnlyList<string> sources, string destDir, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct, bool isMove)
    {
        int succeeded = 0, skipped = 0, failed = 0;
        string? firstError = null;
        long bytesTransferred = 0;
        int doneCount = 0;

        int totalCount = 0;
        foreach (string src in sources)
            totalCount += CountUnits(src);

        foreach (string src in sources)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string dest = Path.Combine(destDir, name);

            bool isDir;
            try { isDir = Directory.Exists(src); }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
                progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "FAIL", src, dest, ex.Message));
                continue;
            }

            if (isDir)
            {
                var sub = await CopyOrMoveDirectoryAsync(src, dest, progress, onConflict, ct, isMove);
                succeeded += sub.Succeeded; skipped += sub.Skipped; failed += sub.Failed;
                firstError ??= sub.FirstError;
                bytesTransferred += sub.Bytes;
                doneCount += sub.Succeeded + sub.Skipped + sub.Failed;
                progress.Report(new FileOpProgressInfo(doneCount, totalCount, bytesTransferred, src));
            }
            else
            {
                var (outcome, bytes) = await CopyOrMoveFileAsync(src, dest, progress, onConflict, ct, isMove);
                bytesTransferred += bytes;
                doneCount++;
                progress.Report(new FileOpProgressInfo(doneCount, totalCount, bytesTransferred, src));
                switch (outcome)
                {
                    case ItemOutcome.Succeeded: succeeded++; break;
                    case ItemOutcome.Skipped: skipped++; break;
                    case ItemOutcome.Failed: failed++; break;
                    case ItemOutcome.Cancelled: throw new OperationCanceledException(ct);
                }
            }
        }

        return new FileOpResult(succeeded, skipped, failed, firstError);
    }

    static int CountUnits(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                int count = 0;
                foreach (string _ in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    count++;
                return Math.Max(count, 1);
            }
        }
        catch { }
        return 1;
    }

    // Conflict check happens immediately before the copy/move attempt (not a pre-scan). A TOCTOU
    // race (external process creates the same-name file between the check and the actual write)
    // surfaces as an IOException from overwrite:false, which is folded into the same onConflict
    // callback below rather than handled by a dedicated exclusion mechanism.
    static async Task<(ItemOutcome, long)> CopyOrMoveFileAsync(
        string src, string dest, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct, bool isMove)
    {
        string finalDest = dest;
        bool overwrite = false;

        if (File.Exists(finalDest) || Directory.Exists(finalDest))
        {
            var decision = await onConflict(Path.GetFileName(finalDest));
            if (decision == null)
                return (ItemOutcome.Cancelled, 0);

            if (decision.Resolution == ConflictResolution.Skip)
            {
                progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "SKIP", src, finalDest, ""));
                return (ItemOutcome.Skipped, 0);
            }
            if (decision.Resolution == ConflictResolution.Rename)
                finalDest = Path.Combine(Path.GetDirectoryName(finalDest) ?? "", decision.NewName ?? Path.GetFileName(finalDest));
            else
                overwrite = true;
        }

        try
        {
            long bytes = isMove
                ? await MoveWithConflictRetryAsync(src, finalDest, overwrite, onConflict, ct)
                : await CopyWithConflictRetryAsync(src, finalDest, overwrite, onConflict, ct);
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "OK", src, finalDest, ""));
            return (ItemOutcome.Succeeded, bytes);
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(finalDest);
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "CANCEL", src, finalDest, ""));
            return (ItemOutcome.Cancelled, 0);
        }
        catch (Exception ex)
        {
            TryDeletePartialFile(finalDest);
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "FAIL", src, finalDest, ex.Message));
            return (ItemOutcome.Failed, 0);
        }
    }

    static async Task<long> MoveWithConflictRetryAsync(
        string src, string dest, bool overwrite, Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            File.Move(src, dest, overwrite);
            return new FileInfo(dest) is { Exists: true } fi ? fi.Length : 0;
        }
        catch (IOException) when (!overwrite)
        {
            var decision = await onConflict(Path.GetFileName(dest));
            if (decision == null) throw new OperationCanceledException(ct);
            if (decision.Resolution == ConflictResolution.Skip) return 0;

            string finalDest = decision.Resolution == ConflictResolution.Rename
                ? Path.Combine(Path.GetDirectoryName(dest) ?? "", decision.NewName ?? Path.GetFileName(dest))
                : dest;
            File.Move(src, finalDest, decision.Resolution == ConflictResolution.Overwrite);
            return new FileInfo(finalDest) is { Exists: true } fi2 ? fi2.Length : 0;
        }
    }

    static async Task<long> CopyWithConflictRetryAsync(
        string src, string dest, bool overwrite, Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct)
    {
        FileMode mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        try
        {
            return await StreamCopyAsync(src, dest, mode, ct);
        }
        catch (IOException) when (!overwrite)
        {
            var decision = await onConflict(Path.GetFileName(dest));
            if (decision == null) throw new OperationCanceledException(ct);
            if (decision.Resolution == ConflictResolution.Skip) return 0;

            string finalDest = decision.Resolution == ConflictResolution.Rename
                ? Path.Combine(Path.GetDirectoryName(dest) ?? "", decision.NewName ?? Path.GetFileName(dest))
                : dest;
            FileMode retryMode = decision.Resolution == ConflictResolution.Overwrite ? FileMode.Create : FileMode.CreateNew;
            return await StreamCopyAsync(src, finalDest, retryMode, ct);
        }
    }

    static async Task<long> StreamCopyAsync(string src, string dest, FileMode mode, CancellationToken ct)
    {
        long total = 0;
        using var sourceStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, CopyFileOptions);
        using var destStream = new FileStream(dest, mode, FileAccess.Write, FileShare.None, CopyBufferSize, CopyFileOptions);
        byte[] buffer = new byte[CopyBufferSize];
        int bytesRead;
        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await destStream.WriteAsync(buffer, 0, bytesRead, ct);
            total += bytesRead;
        }
        return total;
    }

    static void TryDeletePartialFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    static async Task<(int Succeeded, int Skipped, int Failed, string? FirstError, long Bytes)> CopyOrMoveDirectoryAsync(
        string srcDir, string destDir, IFileOpProgress progress,
        Func<string, Task<ConflictDecision?>> onConflict, CancellationToken ct, bool isMove)
    {
        int succeeded = 0, skipped = 0, failed = 0;
        string? firstError = null;
        long bytes = 0;

        try { Directory.CreateDirectory(destDir); }
        catch (Exception ex)
        {
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "FAIL", srcDir, destDir, ex.Message));
            return (0, 0, 1, ex.Message, 0);
        }

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(srcDir); }
        catch (Exception ex)
        {
            progress.ReportItemResult(new OpLogEntry(DateTime.Now, null, isMove ? "MOVE" : "COPY", "FAIL", srcDir, destDir, ex.Message));
            return (0, 0, 1, ex.Message, 0);
        }

        foreach (string entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string entryDest = Path.Combine(destDir, Path.GetFileName(entry));

            if (Directory.Exists(entry))
            {
                var sub = await CopyOrMoveDirectoryAsync(entry, entryDest, progress, onConflict, ct, isMove);
                succeeded += sub.Succeeded; skipped += sub.Skipped; failed += sub.Failed;
                firstError ??= sub.FirstError;
                bytes += sub.Bytes;
            }
            else
            {
                var (outcome, fileBytes) = await CopyOrMoveFileAsync(entry, entryDest, progress, onConflict, ct, isMove);
                bytes += fileBytes;
                switch (outcome)
                {
                    case ItemOutcome.Succeeded: succeeded++; break;
                    case ItemOutcome.Skipped: skipped++; break;
                    case ItemOutcome.Failed: failed++; break;
                    case ItemOutcome.Cancelled: throw new OperationCanceledException(ct);
                }
            }
        }

        if (isMove)
        {
            try { if (!Directory.EnumerateFileSystemEntries(srcDir).Any()) Directory.Delete(srcDir); }
            catch { }
        }

        return (succeeded, skipped, failed, firstError, bytes);
    }
}
```

- [ ] `Program.cs` 先頭の `using` 一覧に `using System.Linq;` が無ければ追加する(`Any()` 拡張メソッドを使うため)。既にあれば何もしない。
- [ ] `dotnet build Zurari.csproj` → exit 0 を確認する。

- [ ] `Zurari.Tests/FileOpEngineTests.cs` を作成する(テンポラリディレクトリに実ファイルを作成して検証)。

```csharp
using Xunit;

public class FileOpEngineTests
{
    sealed class NullProgress : IFileOpProgress
    {
        public void Report(FileOpProgressInfo info) { }
        public void ReportItemResult(OpLogEntry entry) { }
    }

    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zurari-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static Func<string, Task<ConflictDecision?>> NeverAsked()
        => _ => throw new InvalidOperationException("onConflict should not be called");

    [Fact]
    public async Task CopyAsync_SingleFile_CreatesCopyAndKeepsSource()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            string srcFile = Path.Combine(srcDir, "a.txt");
            File.WriteAllText(srcFile, "hello");

            var result = await FileOpEngine.CopyAsync(new[] { srcFile }, destDir, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.True(File.Exists(srcFile));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(destDir, "a.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MoveAsync_SingleFile_RemovesSourceAndCreatesDest()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            string srcFile = Path.Combine(srcDir, "a.txt");
            File.WriteAllText(srcFile, "hello");

            var result = await FileOpEngine.MoveAsync(new[] { srcFile }, destDir, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal(1, result.Succeeded);
            Assert.False(File.Exists(srcFile));
            Assert.True(File.Exists(Path.Combine(destDir, "a.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_Conflict_Overwrite_ReplacesDestFile()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(srcDir, "a.txt"), "new");
            File.WriteAllText(Path.Combine(destDir, "a.txt"), "old");

            var result = await FileOpEngine.CopyAsync(
                new[] { Path.Combine(srcDir, "a.txt") }, destDir, new NullProgress(),
                _ => Task.FromResult<ConflictDecision?>(new ConflictDecision(ConflictResolution.Overwrite)),
                CancellationToken.None);

            Assert.Equal(1, result.Succeeded);
            Assert.Equal("new", File.ReadAllText(Path.Combine(destDir, "a.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_Conflict_Skip_LeavesDestUnchanged()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(srcDir, "a.txt"), "new");
            File.WriteAllText(Path.Combine(destDir, "a.txt"), "old");

            var result = await FileOpEngine.CopyAsync(
                new[] { Path.Combine(srcDir, "a.txt") }, destDir, new NullProgress(),
                _ => Task.FromResult<ConflictDecision?>(new ConflictDecision(ConflictResolution.Skip)),
                CancellationToken.None);

            Assert.Equal(0, result.Succeeded);
            Assert.Equal(1, result.Skipped);
            Assert.Equal("old", File.ReadAllText(Path.Combine(destDir, "a.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_Conflict_Rename_CreatesNewNameAndKeepsOriginal()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(srcDir, "a.txt"), "new");
            File.WriteAllText(Path.Combine(destDir, "a.txt"), "old");

            var result = await FileOpEngine.CopyAsync(
                new[] { Path.Combine(srcDir, "a.txt") }, destDir, new NullProgress(),
                _ => Task.FromResult<ConflictDecision?>(new ConflictDecision(ConflictResolution.Rename, "a (2).txt")),
                CancellationToken.None);

            Assert.Equal(1, result.Succeeded);
            Assert.Equal("old", File.ReadAllText(Path.Combine(destDir, "a.txt")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(destDir, "a (2).txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_Cancelled_LeavesNoPartialFile()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            string srcFile = Path.Combine(srcDir, "big.bin");
            File.WriteAllBytes(srcFile, new byte[1024 * 1024 * 2]);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                FileOpEngine.CopyAsync(new[] { srcFile }, destDir, new NullProgress(), NeverAsked(), cts.Token));

            Assert.False(File.Exists(Path.Combine(destDir, "big.bin")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_MultipleSources_AllSucceed()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(srcDir, "a.txt"), "a");
            File.WriteAllText(Path.Combine(srcDir, "b.txt"), "b");
            File.WriteAllText(Path.Combine(srcDir, "c.txt"), "c");

            var result = await FileOpEngine.CopyAsync(
                new[] { Path.Combine(srcDir, "a.txt"), Path.Combine(srcDir, "b.txt"), Path.Combine(srcDir, "c.txt") },
                destDir, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal(3, result.Succeeded);
            Assert.True(File.Exists(Path.Combine(destDir, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir, "b.txt")));
            Assert.True(File.Exists(Path.Combine(destDir, "c.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_OneFileFailsAmongMany_ContinuesAndReportsFailure()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(srcDir, "a.txt"), "a");
            string missing = Path.Combine(srcDir, "missing.txt"); // never created -> read failure
            File.WriteAllText(Path.Combine(srcDir, "c.txt"), "c");

            var result = await FileOpEngine.CopyAsync(
                new[] { Path.Combine(srcDir, "a.txt"), missing, Path.Combine(srcDir, "c.txt") },
                destDir, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal(2, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.NotNull(result.FirstError);
            Assert.True(File.Exists(Path.Combine(destDir, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir, "c.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_Directory_RecursesAndPreservesStructure()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destParent = Path.Combine(root, "dest");
            Directory.CreateDirectory(destParent);
            string nested = Path.Combine(srcDir, "sub");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(srcDir, "top.txt"), "top");
            File.WriteAllText(Path.Combine(nested, "deep.txt"), "deep");

            var result = await FileOpEngine.CopyAsync(new[] { srcDir }, destParent, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal(2, result.Succeeded);
            string copiedDir = Path.Combine(destParent, "src");
            Assert.True(File.Exists(Path.Combine(copiedDir, "top.txt")));
            Assert.True(File.Exists(Path.Combine(copiedDir, "sub", "deep.txt")));
            Assert.True(Directory.Exists(srcDir)); // copy keeps source
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MoveAsync_Directory_RemovesSourceDirAfterMove()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destParent = Path.Combine(root, "dest");
            Directory.CreateDirectory(destParent);
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "top.txt"), "top");

            var result = await FileOpEngine.MoveAsync(new[] { srcDir }, destParent, new NullProgress(), NeverAsked(), CancellationToken.None);

            Assert.Equal(1, result.Succeeded);
            Assert.False(Directory.Exists(srcDir));
            Assert.True(File.Exists(Path.Combine(destParent, "src", "top.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
}
```

- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter FileOpEngineTests` → `Passed!`(実装はこのステップより前に済ませているため、ここでは green を確認する。もし失敗があれば `FileOpEngine` 側の実装ミスなので修正する)。
- [ ] `dotnet build Zurari.csproj` → exit 0。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!`(既存テストを含め全緑)。
- [ ] コミット: `git add Program.cs Zurari.Tests/FileOpEngineTests.cs && git commit -m "feat(phase7): add FileOpEngine copy/move engine with conflict handling"`

---

### Task 2: JobManager と BackgroundJob

**Files:**
- Modify: `C:\Users\user\source\repos\panzoux\zurari\Program.cs`(`FileOpEngine` の直後に追加)
- Create: `C:\Users\user\source\repos\panzoux\zurari\Zurari.Tests\JobManagerTests.cs`

**手順:**

- [ ] `Program.cs` の `FileOpEngine` の直後に以下を追加する。ガイドの API 契約(Phase 7 節)のシグネチャを厳守する。

```csharp
enum JobStatus { Pending, Running, Completed, Failed, Cancelled }

sealed class BackgroundJob
{
    public int ShortId;
    public string OpName = ""; // "コピー" / "移動"
    public JobStatus Status;
    public int DoneCount;
    public int TotalCount;
    public long BytesTransferred;
    public string CurrentFile = "";
    public CancellationTokenSource Cts = new();
    public List<string> SourcePaths = new();
    public string DestDir = "";
    public FileOpResult? Result;
    public DateTime StartTime;
    public DateTime? EndTime;
}

static class JobManager
{
    const int MaxConcurrentJobs = 4;
    const int MaxCompletedHistory = 20;

    public static readonly object ProgressLock = new object();

    static readonly object _lock = new object();
    static readonly List<BackgroundJob> _jobs = new();
    static readonly SemaphoreSlim _semaphore = new(MaxConcurrentJobs);
    static int _nextShortId = 1;

    public static BackgroundJob Start(string opName, IReadOnlyList<string> sources, string destDir,
        Func<BackgroundJob, CancellationToken, Task<FileOpResult>> work)
    {
        var job = new BackgroundJob
        {
            OpName = opName,
            Status = JobStatus.Pending,
            SourcePaths = new List<string>(sources),
            DestDir = destDir,
            StartTime = DateTime.Now,
        };

        lock (_lock)
        {
            job.ShortId = _nextShortId++;
            _jobs.Add(job);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _semaphore.WaitAsync(job.Cts.Token);
                try
                {
                    lock (_lock) { job.Status = JobStatus.Running; }
                    FileOpResult result = await work(job, job.Cts.Token);
                    lock (_lock)
                    {
                        job.Result = result;
                        job.Status = JobStatus.Completed;
                        job.EndTime = DateTime.Now;
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                lock (_lock) { job.Status = JobStatus.Cancelled; job.EndTime = DateTime.Now; }
            }
            catch (Exception)
            {
                lock (_lock) { job.Status = JobStatus.Failed; job.EndTime = DateTime.Now; }
            }
            finally
            {
                TrimCompletedHistory();
            }
        });

        return job;
    }

    public static List<BackgroundJob> Snapshot()
    {
        lock (_lock) { return new List<BackgroundJob>(_jobs); }
    }

    public static void Cancel(int shortId)
    {
        lock (_lock) { _jobs.Find(j => j.ShortId == shortId)?.Cts.Cancel(); }
    }

    public static List<string> GetBusyPaths()
    {
        lock (_lock)
        {
            var paths = new List<string>();
            foreach (var job in _jobs)
            {
                if (job.Status != JobStatus.Pending && job.Status != JobStatus.Running) continue;
                paths.AddRange(job.SourcePaths);
                paths.Add(job.DestDir);
            }
            return paths;
        }
    }

    public static bool HasActiveJobs
    {
        get { lock (_lock) { return _jobs.Exists(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running); } }
    }

    public static async Task CancelAllAndWaitAsync(TimeSpan timeout)
    {
        List<BackgroundJob> active;
        lock (_lock) { active = _jobs.FindAll(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running); }
        foreach (var job in active) job.Cts.Cancel();

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            bool anyActive;
            lock (_lock) { anyActive = _jobs.Exists(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running); }
            if (!anyActive) return;
            await Task.Delay(50);
        }
    }

    static void TrimCompletedHistory()
    {
        lock (_lock)
        {
            var completed = _jobs.FindAll(j => j.Status == JobStatus.Completed || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled);
            if (completed.Count <= MaxCompletedHistory) return;

            completed.Sort((a, b) => (a.EndTime ?? DateTime.MinValue).CompareTo(b.EndTime ?? DateTime.MinValue));
            int toRemove = completed.Count - MaxCompletedHistory;
            for (int i = 0; i < toRemove; i++)
                _jobs.Remove(completed[i]);
        }
    }
}
```

  `JobManager.ProgressLock` は Task 4 で `IFileOpProgress` 実装(`JobFileOpProgress`)が `BackgroundJob` の進捗フィールドへ書き込む際に使う共有ロックとして公開する(spec の「進捗フィールドへの書き込みは lock 保護」を、`JobManager` 内部の `_lock` とは別の専用ロックで満たす)。

- [ ] `dotnet build Zurari.csproj` → exit 0。

- [ ] `Zurari.Tests/JobManagerTests.cs` を作成する。

```csharp
using Xunit;

public class JobManagerTests
{
    static Task<FileOpResult> Instant(BackgroundJob job, CancellationToken ct)
        => Task.FromResult(new FileOpResult(1, 0, 0, null));

    static async Task<FileOpResult> WaitForCancel(BackgroundJob job, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new FileOpResult(0, 0, 0, null);
    }

    static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within timeout");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Start_JobCompletesSuccessfully_StatusBecomesCompleted()
    {
        var job = JobManager.Start("コピー", new[] { "a" }, "dest", Instant);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Completed);

        var snap = JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!;
        Assert.Equal(JobStatus.Completed, snap.Status);
        Assert.NotNull(snap.Result);
        Assert.Equal(1, snap.Result!.Succeeded);
    }

    [Fact]
    public async Task Start_AssignsIncrementingShortIds()
    {
        var job1 = JobManager.Start("コピー", new[] { "a" }, "dest", Instant);
        var job2 = JobManager.Start("コピー", new[] { "b" }, "dest", Instant);
        Assert.True(job2.ShortId > job1.ShortId);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job1.ShortId)!.Status == JobStatus.Completed);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job2.ShortId)!.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task Cancel_RunningJob_TransitionsToCancelled()
    {
        var job = JobManager.Start("移動", new[] { "a" }, "dest", WaitForCancel);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Running);

        JobManager.Cancel(job.ShortId);

        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Cancelled);
    }

    [Fact]
    public async Task GetBusyPaths_ReturnsSourcesAndDestOfActiveJobsOnly()
    {
        var job = JobManager.Start("コピー", new[] { @"C:\src\a.txt", @"C:\src\b.txt" }, @"C:\dest", WaitForCancel);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Running);

        var busy = JobManager.GetBusyPaths();
        Assert.Contains(@"C:\src\a.txt", busy);
        Assert.Contains(@"C:\src\b.txt", busy);
        Assert.Contains(@"C:\dest", busy);

        JobManager.Cancel(job.ShortId);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Cancelled);
    }

    [Fact]
    public async Task CancelAllAndWaitAsync_WaitsForCancellationWithinTimeout()
    {
        var job = JobManager.Start("移動", new[] { "a" }, "dest", WaitForCancel);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Running);

        await JobManager.CancelAllAndWaitAsync(TimeSpan.FromSeconds(5));

        var snap = JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!;
        Assert.Equal(JobStatus.Cancelled, snap.Status);
    }

    [Fact]
    public async Task ConcurrencyLimit_FifthJobStaysPendingUntilSlotFrees()
    {
        var gate = new TaskCompletionSource();
        Task<FileOpResult> Blocking(BackgroundJob job, CancellationToken ct) => WaitForGate(gate);
        static async Task<FileOpResult> WaitForGate(TaskCompletionSource g)
        {
            await g.Task;
            return new FileOpResult(1, 0, 0, null);
        }

        var j1 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        var j2 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        var j3 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        var j4 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        var j5 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        int[] ids = { j1.ShortId, j2.ShortId, j3.ShortId, j4.ShortId, j5.ShortId };

        await WaitUntilAsync(() =>
        {
            var snap = JobManager.Snapshot();
            int running = 0;
            foreach (int id in ids)
                if (snap.Find(j => j.ShortId == id)!.Status == JobStatus.Running) running++;
            return running == 4;
        });

        Assert.Equal(JobStatus.Pending, JobManager.Snapshot().Find(j => j.ShortId == j5.ShortId)!.Status);

        gate.SetResult();

        await WaitUntilAsync(() =>
        {
            var snap = JobManager.Snapshot();
            foreach (int id in ids)
                if (snap.Find(j => j.ShortId == id)!.Status != JobStatus.Completed) return false;
            return true;
        }, timeoutMs: 5000);
    }
}
```

  補足: `JobManager` は static 共有状態を持つため、xUnit のクラス間並列実行と `ConcurrencyLimit_...` テスト(4スロットを占有する)が衝突し flaky になる可能性がある。まずはそのまま実行し、不安定であれば「未解決事項」に記録したうえで `Zurari.Tests` に `[assembly: CollectionBehavior(DisableTestParallelization = true)]` を追加する対応を検討する(このタスクでは追加しない)。

- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter JobManagerTests` → `Passed!`。
- [ ] `dotnet build Zurari.csproj` → exit 0。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!`(全緑)。
- [ ] コミット: `git add Program.cs Zurari.Tests/JobManagerTests.cs && git commit -m "feat(phase7): add JobManager with concurrency limit, cancellation, busy path tracking"`

---

### Task 3: ダイアログ要求キュー + RequestConflictAsync

**Files:**
- Modify: `C:\Users\user\source\repos\panzoux\zurari\Program.cs`

**手順:**

- [ ] `Program` クラス内の static フィールド群(`static readonly object _searchLock = new object();` の近く)に以下を追加する。

```csharp
static readonly object _dialogQueueLock = new object();
static readonly Queue<Action> _dialogRequestQueue = new();
static readonly Dictionary<int, ConflictResolution> _jobStickyChoice = new();
```

- [ ] `Program` の static メソッド群に `RequestConflictAsync` を追加する(配置は `JobManager` 型定義の直後、`Program` クラス内)。ジョブ単位の sticky 選択(「すべて上書き」「すべてスキップ」)を記憶し、2回目以降はダイアログを開かず即答する。

```csharp
static Task<ConflictDecision?> RequestConflictAsync(int jobShortId, string destName)
{
    var tcs = new TaskCompletionSource<ConflictDecision?>();

    lock (_dialogQueueLock)
    {
        if (_jobStickyChoice.TryGetValue(jobShortId, out ConflictResolution sticky))
        {
            tcs.SetResult(new ConflictDecision(sticky));
            return tcs.Task;
        }

        _dialogRequestQueue.Enqueue(() =>
        {
            var options = new List<string> { "上書き", "すべて上書き", "スキップ", "すべてスキップ", "リネーム" };
            ShowListSelect($"#{jobShortId} 衝突: {destName}", options, choice =>
            {
                switch (choice)
                {
                    case null:
                        tcs.SetResult(null); // Esc = ジョブキャンセル
                        break;
                    case "上書き":
                        tcs.SetResult(new ConflictDecision(ConflictResolution.Overwrite));
                        break;
                    case "すべて上書き":
                        lock (_dialogQueueLock) { _jobStickyChoice[jobShortId] = ConflictResolution.Overwrite; }
                        tcs.SetResult(new ConflictDecision(ConflictResolution.Overwrite));
                        break;
                    case "スキップ":
                        tcs.SetResult(new ConflictDecision(ConflictResolution.Skip));
                        break;
                    case "すべてスキップ":
                        lock (_dialogQueueLock) { _jobStickyChoice[jobShortId] = ConflictResolution.Skip; }
                        tcs.SetResult(new ConflictDecision(ConflictResolution.Skip));
                        break;
                    case "リネーム":
                        ShowTextInput("新しい名前", destName, newName =>
                        {
                            tcs.SetResult(newName == null ? null : new ConflictDecision(ConflictResolution.Rename, newName));
                        });
                        break;
                }
            });
        });
    }

    return tcs.Task;
}
```

  `_jobStickyChoice` のエントリはジョブ完了後も辞書に残るが、キーは増分の `ShortId` でありプロセス寿命中のジョブ数分しか増えないため実用上無視できる。気になる場合の削除タイミング(ジョブ完了検知時など)は Task 6 の完了検知処理に合わせて追加してもよいが、本計画では必須としない。

- [ ] メインループ(`Main` 内 `while (true)` ループの `HandleResizeIfNeeded();` の直後、`Draw();` の直前)に、ダイアログキューから1件取り出す処理を挿入する。`Draw()` が `State.Dialog` を見て描画するため、`Draw()` より前に確定させる必要がある。

```csharp
while (true)
{
    HandleResizeIfNeeded();

    if (State.Dialog == null)
    {
        Action? next = null;
        lock (_dialogQueueLock)
        {
            if (_dialogRequestQueue.Count > 0)
                next = _dialogRequestQueue.Dequeue();
        }
        next?.Invoke();
    }

    Draw();
    // ...(既存のまま)
```

- [ ] `dotnet build Zurari.csproj` → exit 0。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!`(既存 + Task1/2 のテストとも緑のまま)。
- [ ] 手動確認は Task 4 でジョブ登録経路ができてから行う(このタスク単体では衝突を発生させる経路がまだ無い)。
- [ ] コミット: `git add Program.cs && git commit -m "feat(phase7): add dialog request queue and RequestConflictAsync"`

---

### Task 4: c/m キー — 宛先解決とジョブ登録

**Files:**
- Modify: `C:\Users\user\source\repos\panzoux\zurari\Program.cs`

**手順:**

- [ ] 着手前に `Program.cs` を検索し、前提条件で確認した「マークがあれば集合・なければカーソル下1件の絶対パス一覧を返す関数」と「ブックマーク+ヒストリの結合済み一覧を返す関数」の実際の名前とシグネチャを確定する。以下のコードでは仮に `GetOperationTargets(Pane pane)`(戻り値 `List<string>`)と `BuildBookmarkAndHistoryList()`(戻り値 `List<string>`)として記述する。実際の名前が異なれば、以降のコードのその呼び出し箇所だけを実物の名前に置き換える。

- [ ] `HandleSpecialKeyAsync` の `switch (key.Key)` に `C` と `M` のケースを追加する(`case ConsoleKey.F3:` の直後に置く)。`HandleSpecialKeyAsync` は Phase 1 リファクタリングにより先頭引数 `Pane pane` を取る前提。

```csharp
case ConsoleKey.C:
    if (key.Modifiers == ConsoleModifiers.None)
        await StartCopyOrMoveAsync(pane, isMove: false);
    break;

case ConsoleKey.M:
    if (key.Modifiers == ConsoleModifiers.None)
        await StartCopyOrMoveAsync(pane, isMove: true);
    break;
```

- [ ] `HandleSpecialKeyAsync` の直後に以下を追加する。

```csharp
static async Task StartCopyOrMoveAsync(Pane pane, bool isMove)
{
    List<string> targets = GetOperationTargets(pane);
    if (targets.Count == 0)
    {
        _lastErrorMessage = isMove ? "移動対象がありません" : "コピー対象がありません";
        return;
    }

    string opName = isMove ? "移動" : "コピー";

    if (State.SplitActive)
    {
        int otherPaneIndex = State.FocusedPane == 0 ? 1 : 0;
        Pane otherPane = State.Panes[otherPaneIndex];
        string destDir = otherPane.Columns[otherPane.ActiveColumn].Path;

        if (destDir == "::DRIVES::")
        {
            _lastErrorMessage = "ドライブ一覧を宛先にはできません";
            return;
        }

        EnqueueCopyOrMoveJob(opName, targets, destDir, isMove);
    }
    else
    {
        List<string> candidates = BuildBookmarkAndHistoryList();
        ShowListSelect("宛先を選択", candidates, dest =>
        {
            if (dest == null)
            {
                _lastErrorMessage = $"{opName}をキャンセルしました";
                return;
            }
            if (dest == "::DRIVES::")
            {
                _lastErrorMessage = "ドライブ一覧を宛先にはできません";
                return;
            }
            EnqueueCopyOrMoveJob(opName, targets, dest, isMove);
        });
    }

    await Task.CompletedTask;
}

static void EnqueueCopyOrMoveJob(string opName, List<string> targets, string destDir, bool isMove)
{
    // busy パス警告(実行中ジョブと対象パスの重なり確認)は spec 通り Phase 8 で本関数の冒頭に追加する。
    // Phase 7 時点では警告なしで開始してよい(重なった場合の競合はエンジンの「失敗を記録して継続」が受け止める)。
    // Phase 8 が使う判定ヘルパ(PathOverlapHelper.PathsOverlap)もこの Phase では実装しない。
    BackgroundJob job = JobManager.Start(opName, targets, destDir, (jobRef, ct) =>
    {
        var progress = new JobFileOpProgress(jobRef);
        Task<ConflictDecision?> OnConflict(string destName) => RequestConflictAsync(jobRef.ShortId, destName);
        return isMove
            ? FileOpEngine.MoveAsync(targets, destDir, progress, OnConflict, ct)
            : FileOpEngine.CopyAsync(targets, destDir, progress, OnConflict, ct);
    });

    _lastErrorMessage = $"{opName}開始: {targets.Count}件 → {destDir} (#{job.ShortId})";
}

sealed class JobFileOpProgress : IFileOpProgress
{
    readonly BackgroundJob _job;
    public JobFileOpProgress(BackgroundJob job) { _job = job; }

    public void Report(FileOpProgressInfo info)
    {
        lock (JobManager.ProgressLock)
        {
            _job.DoneCount = info.DoneCount;
            _job.TotalCount = info.TotalCount;
            _job.BytesTransferred = info.BytesTransferred;
            _job.CurrentFile = info.CurrentFile;
        }
    }

    public void ReportItemResult(OpLogEntry entry)
    {
        OperationLog.Append(entry with { JobId = _job.ShortId });
    }
}
```

  `OpLogEntry` は `sealed record`(Phase 4 導入)なので `with` 式が使える。`FileOpEngine` は `JobId: null` で `OpLogEntry` を作るため、ここで実ジョブの `ShortId` に上書きしてから `OperationLog.Append` する。

- [ ] `Program.cs` 先頭の `using` に `using System.Linq;` があることを確認する(Task 1 で確認済みのはず)。
- [ ] `dotnet build Zurari.csproj` → exit 0。コンパイルエラーが `GetOperationTargets` / `BuildBookmarkAndHistoryList` の未定義に起因する場合は、実際の関数名に置き換えて再ビルドする。
- [ ] 手動確認: 分割モード(`Ctrl+W`)を有効にし、上ペインでファイルを1つ選択して `c` を押す。「コピー開始: 1件 → <下ペインのパス> (#1)」がステータス行に一瞬表示され、直後にカーソル移動などの操作が即座にできる(ブロックされない)ことを確認する。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!`。
- [ ] コミット: `git add Program.cs && git commit -m "feat(phase7): wire c/m keys to destination resolution and job registration"`

---

### Task 5: ステータス行ジョブサマリ

**Files:**
- Modify: `C:\Users\user\source\repos\panzoux\zurari\Program.cs`

**手順:**

- [ ] `Program` の static フィールド群に間引き用の状態を追加する。

```csharp
static DateTime _lastJobSummaryRenderTime = DateTime.MinValue;
static string? _cachedJobSummary = null;
```

- [ ] `BuildFrame` のステータス行分岐で、spec の優先順位「Leap バー > Search バー > エラー/one-shot メッセージ > ジョブサマリ > 通常ヒント」に従い、`_lastErrorMessage` 分岐と通常ヒント分岐(`else { ... }`)の間に `TryBuildJobSummary` の分岐を挿入する。

```csharp
else if (_lastErrorMessage != null)
{
    string status = _lastErrorMessage;
    _lastErrorMessage = null;  // Clear error after displaying
    status = CharacterWidth.SmartTruncate(status, width);
    frame[height - 1] = CharacterWidth.PadToWidth(status, width);
}
else if (TryBuildJobSummary(width, out string jobSummary))
{
    frame[height - 1] = jobSummary;
}
else
{
    string previewHint = State.Preview.IsVisible ? "Shift+V=Preview[on]" : "Shift+V=Preview[off]";
    // ...(既存のまま、変更不要)
}
```

- [ ] `BuildFrame` の直後に `TryBuildJobSummary` を追加する。

```csharp
static bool TryBuildJobSummary(int width, out string summary)
{
    List<BackgroundJob> active = JobManager.Snapshot().FindAll(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running);

    if (active.Count == 0)
    {
        summary = "";
        return false;
    }

    if (_cachedJobSummary == null || (DateTime.UtcNow - _lastJobSummaryRenderTime).TotalMilliseconds >= 100)
    {
        active.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        BackgroundJob oldest = active[0];

        int doneCount, totalCount;
        string currentFile;
        lock (JobManager.ProgressLock)
        {
            doneCount = oldest.DoneCount;
            totalCount = oldest.TotalCount;
            currentFile = oldest.CurrentFile;
        }

        int percent = totalCount > 0 ? (int)(100.0 * doneCount / totalCount) : 0;
        string text = $"▶{active.Count}件実行中 #{oldest.ShortId} {oldest.OpName} {percent}% ({doneCount}/{totalCount}) {currentFile}";
        text = CharacterWidth.SmartTruncate(text, width);
        _cachedJobSummary = CharacterWidth.PadToWidth(text, width);
        _lastJobSummaryRenderTime = DateTime.UtcNow;
    }

    summary = _cachedJobSummary!;
    return true;
}
```

- [ ] `dotnet build Zurari.csproj` → exit 0。
- [ ] 手動確認: テンポラリに数百MB程度のファイルを作り分割モードでコピーし、ステータス行に「▶1件実行中 #1 コピー NN% (x/y) <ファイル名>」が表示され、進捗率が滑らかに増えていく(体感でチラつかない)ことを確認する。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!`。
- [ ] コミット: `git add Program.cs && git commit -m "feat(phase7): show job summary on status line with 100ms throttling"`

---

### Task 6: 完了時反映(結果サマリ・影響カラム再読込)

**Files:**
- Modify: `C:\Users\user\source\repos\panzoux\zurari\Program.cs`

**手順:**

- [ ] `Program` の static フィールド群に差分検出・スロットリング用の状態を追加する。

```csharp
static readonly Dictionary<int, JobStatus> _lastKnownJobStatus = new();
static DateTime _lastActiveJobRefreshTime = DateTime.MinValue;
```

- [ ] メインループ内、Task 3 で追加した「ダイアログキューの Dequeue」ブロックの直後・`Draw();` の直前に、ジョブ完了検知と実行中の間引き再読込を追加する。

```csharp
    if (State.Dialog == null)
    {
        Action? next = null;
        lock (_dialogQueueLock)
        {
            if (_dialogRequestQueue.Count > 0)
                next = _dialogRequestQueue.Dequeue();
        }
        next?.Invoke();
    }

    await ProcessJobCompletionsAsync();
    await RefreshActiveJobColumnsAsync();

    Draw();
```

- [ ] `Main` の直後に `ProcessJobCompletionsAsync` を追加する。前回スナップショットとの状態差分で「新たに完了したジョブ」を検出し、one-shot 結果サマリを表示したうえで影響カラムを再読込する。

```csharp
static async Task ProcessJobCompletionsAsync()
{
    List<BackgroundJob> snapshot = JobManager.Snapshot();
    var newlyFinished = new List<BackgroundJob>();

    foreach (var job in snapshot)
    {
        bool isFinished = job.Status == JobStatus.Completed || job.Status == JobStatus.Failed || job.Status == JobStatus.Cancelled;
        bool wasKnown = _lastKnownJobStatus.TryGetValue(job.ShortId, out JobStatus prevStatus);

        if (isFinished && (!wasKnown || prevStatus != job.Status))
            newlyFinished.Add(job);

        _lastKnownJobStatus[job.ShortId] = job.Status;
    }

    foreach (var job in newlyFinished)
    {
        _lastErrorMessage = BuildJobResultSummary(job);
        await RefreshAffectedColumnsAsync(job);
    }
}

static string BuildJobResultSummary(BackgroundJob job)
{
    if (job.Status == JobStatus.Cancelled)
    {
        int done = job.Result?.Succeeded ?? job.DoneCount;
        return $"キャンセル: {done}/{job.TotalCount}件完了";
    }

    FileOpResult? result = job.Result;
    if (result == null)
        return $"{job.OpName} #{job.ShortId}: 完了";

    if (result.Failed > 0)
        return $"{job.OpName} #{job.ShortId}: {result.Succeeded}件完了 / {result.Failed}件失敗: {result.FirstError}";
    if (result.Skipped > 0)
        return $"{job.OpName} #{job.ShortId}: {result.Succeeded}件完了 / {result.Skipped}件スキップ";
    return $"{job.OpName}完了: {result.Succeeded}件";
}
```

- [ ] 「影響カラム = Path が宛先 or ソース親と `OrdinalIgnoreCase` 一致」の判定と、Leap/Search アクティブペインへの `NeedsRefresh` 立てを行う `RefreshAffectedColumnsAsync` を追加する。`RebuildRightSideAsync` は Phase 1 リファクタリングにより `Pane pane` を先頭引数に取る前提(実際のシグネチャを確認し、引数順が異なる場合はそちらに合わせる)。

```csharp
static async Task RefreshAffectedColumnsAsync(BackgroundJob job)
{
    var affectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { job.DestDir };
    foreach (string src in job.SourcePaths)
    {
        string? parent = Path.GetDirectoryName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent != null)
            affectedDirs.Add(parent);
    }

    foreach (Pane pane in State.Panes)
    {
        if (!pane.Columns.Exists(c => affectedDirs.Contains(c.Path)))
            continue;

        if (pane.Leap.Active || pane.Search.Active)
        {
            pane.NeedsRefresh = true;
            continue;
        }

        await RefreshPaneNowAsync(pane, affectedDirs);
    }
}

static async Task RefreshPaneNowAsync(Pane pane, HashSet<string>? onlyDirs = null)
{
    int deepestAffected = -1;
    for (int i = 0; i < pane.Columns.Count; i++)
    {
        if (onlyDirs == null || onlyDirs.Contains(pane.Columns[i].Path))
        {
            pane.Columns[i].CachedChildren.Clear();
            deepestAffected = i;
        }
    }
    if (deepestAffected < 0) deepestAffected = 0;
    await RebuildRightSideAsync(pane, deepestAffected);
}
```

- [ ] `RefreshActiveJobColumnsAsync`(実行中の 500ms スロットリング再読込)を追加する。

```csharp
static async Task RefreshActiveJobColumnsAsync()
{
    List<BackgroundJob> active = JobManager.Snapshot().FindAll(j => j.Status == JobStatus.Running);
    if (active.Count == 0) return;

    if ((DateTime.UtcNow - _lastActiveJobRefreshTime).TotalMilliseconds < 500)
        return;
    _lastActiveJobRefreshTime = DateTime.UtcNow;

    foreach (BackgroundJob job in active)
        await RefreshAffectedColumnsAsync(job);
}
```

- [ ] `ExitSearchMode(Pane pane, bool restoreCursor)` の本体を実際に開き、既存処理の末尾(関数の最後)に次を追記する(既存コードの削除・変更はしない、追記のみ)。

```csharp
    if (pane.NeedsRefresh)
    {
        pane.NeedsRefresh = false;
        _ = RefreshPaneNowAsync(pane);
    }
}
```

  `ExitSearchMode` は同期 `void` のため、非同期の再読込は `_ = RefreshPaneNowAsync(pane);`(fire-and-forget)にする。

- [ ] `ExitLeapModeAsync(Pane pane, bool restoreCursor)` の本体を実際に開き、既存の末尾(`await RebuildRightSideAsync(pane, ...); if (pane.Preview.IsVisible) StartPreviewLoad();` の直後)に次を追記する。

```csharp
    if (pane.NeedsRefresh)
    {
        pane.NeedsRefresh = false;
        await RefreshPaneNowAsync(pane);
    }
}
```

  **重要**: このステップの着手前に `ExitSearchMode` と `ExitLeapModeAsync` の Phase 1〜6 リファクタリング後の実際の本体を `Program.cs` で読み、上記の追記コードをその関数の閉じ括弧の直前に挿入すること。引数名が `pane` でなければそれに合わせる。

- [ ] `dotnet build Zurari.csproj` → exit 0。
- [ ] 手動確認1: 分割モードで小さいファイル1件をコピーし、完了後にステータス行へ「コピー完了: 1件」相当が表示され、宛先ペインのカラムに新しいファイルが自動的に現れることを確認する。
- [ ] 手動確認2: コピー先ディレクトリで検索モード(`/`)を有効にした状態で同ディレクトリへの別のコピーを完了させ、検索モード中はカラムが変化せず、`Esc` で検索を終了した瞬間にカラムが更新されることを確認する。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!`。
- [ ] コミット: `git add Program.cs && git commit -m "feat(phase7): refresh affected columns and show result summary on job completion"`

---

### Task 7: 終了確認(アクティブジョブがある状態の Esc)

**Files:**
- Modify: `C:\Users\user\source\repos\panzoux\zurari\Program.cs`

**手順:**

- [ ] `Program` の static フィールド群に追加する。

```csharp
static bool _shouldExit = false;
```

- [ ] `Main` の `while (true)` ループ先頭(`HandleResizeIfNeeded();` の直前)に脱出チェックを追加する。

```csharp
while (true)
{
    if (_shouldExit)
        break;

    HandleResizeIfNeeded();
    // ...(既存のまま)
```

- [ ] メインループ内の通常状態(ダイアログ・Leap・Search いずれも非アクティブ)での Esc 処理(`switch (key.KeyChar) { case (char)27: return; ... }`)を次のように変更する。ダイアログ/Leap/Search がアクティブなときの Esc は各ハンドラが個別に処理済みであり、ここで変更するのは「どのモードにも属さない通常状態での Esc」のみである。

```csharp
case (char)27: // Esc
    if (JobManager.HasActiveJobs)
    {
        int activeCount = JobManager.Snapshot().FindAll(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running).Count;
        ShowConfirm("終了確認", $"実行中のジョブが{activeCount}件あります。すべてキャンセルして終了しますか?", async result =>
        {
            if (result == true)
            {
                await JobManager.CancelAllAndWaitAsync(TimeSpan.FromSeconds(5));
                _shouldExit = true;
            }
        });
    }
    else
    {
        return;
    }
    break;
```

  `ShowConfirm` のコールバックは `Action<bool?>` だが、ここでは `async result => {...}` を渡す(`async void` 相当)。`CancelAllAndWaitAsync` はタイムアウトで必ず抜ける設計であり、この経路で未処理例外が飛ぶことは想定しない。

- [ ] `Main` の `finally` ブロックの先頭に、保険としての全ジョブキャンセル+待機を追加する(`Main` は `async Task` のため `finally` 内 `await` は問題ない)。

```csharp
finally
{
    if (JobManager.HasActiveJobs)
        await JobManager.CancelAllAndWaitAsync(TimeSpan.FromSeconds(5));

    try { Console.CursorVisible = true; } catch { }
    Console.Write("\x1b[?1049l"); // Leave alternate screen buffer
    _migemo?.Dispose();
}
```

- [ ] `dotnet build Zurari.csproj` → exit 0。
- [ ] 手動確認1: 大きめのファイルをコピー中(ジョブが Running のうち)に `Esc` を押し、「実行中のジョブが1件あります。すべてキャンセルして終了しますか?」が表示されること、`N`/`Esc` で確認を閉じてアプリに戻れること、`Y`/`Enter` で数秒以内にプロセスが終了することを確認する。
- [ ] 手動確認2: ジョブが無い状態で `Esc` を押すと確認なしで即座に終了することを確認する(既存動作を壊していないこと)。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!`。
- [ ] コミット: `git add Program.cs && git commit -m "feat(phase7): confirm before exiting with active jobs and cancel-all on shutdown"`

---

## 完了条件チェックリスト

- [x] `c`/`m` が UI をブロックしない(登録直後に制御が返り、直後のキー入力・描画が即座に反応する)。コード上確認済み(`JobManager.Start` は `Task.Run` を発火して同期的に return、`StartCopyOrMoveAsync` はダイアログ待ちを await しない)。実機での体感確認は未実施(下記未解決事項参照)。
- [x] 単一ファイルのコピー/移動が完了する(Task 1, 4)。`FileOpEngineTests` で実ファイルシステムに対して検証済み。
- [x] マーク複数件のコピー/移動が完了する(Task 1, 4。`GetOperationTargets` がマーク集合を返す)。
- [x] ディレクトリ再帰のコピー/移動が完了する(Task 1 の `FileOpEngineTests`)。
- [x] 進捗がステータス行のジョブサマリに表示され、100ms 未満では再計算されない(Task 5)。`RedrawGate`/`UiActivityInProgress` 経由で `JobManager.HasActiveJobs` 中は継続再描画されるよう拡張済み(plan 未記載の実装、下記参照)。
- [x] 衝突ダイアログが「上書き/すべて上書き/スキップ/すべてスキップ/リネーム」の5択で機能し、sticky が次回以降ダイアログを開かず即答する(Task 3)。
- [x] 衝突ダイアログの Esc がジョブ全体のキャンセルとして扱われる(Task 3 の `null` 結果 → Task 1 の `CopyOrMoveFileAsync` が `OperationCanceledException` に変換)。
- [x] 完了時に結果サマリが one-shot でステータス行に表示される(Task 6)。`_redrawGate.Request()` 追加により実際に画面に反映されることを確認済み(初回実装は漏れており、レビューで検出・修正)。
- [x] 完了時に影響カラム(宛先・ソース親、`OrdinalIgnoreCase`)のみが再読込される(Task 6)。
- [x] Leap/Search アクティブなペインは自動再読込されず、モード終了時に反映される(Task 6、`ExitSearchMode`/`ExitLeapModeAsync` への追記)。
- [x] ファイル単位の結果が `OperationLog` に記録される(Task 1 の `ReportItemResult` 呼び出し + Task 4 の `JobFileOpProgress.ReportItemResult`)。
- [x] アクティブジョブがある状態の `Esc` で終了確認ダイアログが出て、Yes で全ジョブキャンセル+最大5秒待機後に終了する(Task 7)。
- [x] `FileOpEngine` と `JobManager` のユニットテスト(複数件・失敗継続・キャンセル・同時実行上限4・busy パス前方一致・完了20件保持)がすべて緑(Task 1, 2)。
- [x] `dotnet build Zurari.csproj` が最終状態で exit 0。
- [x] `dotnet test Zurari.Tests/Zurari.Tests.csproj` が最終状態で `Passed!`(全テスト、153件)。

## 未解決事項

実行セッション(2026-08-02, subagent-driven-development)が記録する判断保留・発見事項:

1. **`BuildBookmarkAndHistoryList()` は Phase 4 導入済みという前提が誤りだった。** 実際のコードにはブックマーク(`Bookmarks.Load()`)とヒストリ(`_history`)を結合する関数は存在せず、`b`/`h` キーでそれぞれ別々に開くのみだった。Task 4 で新規に追加(ブックマーク優先、ヒストリは新しい順、`OrdinalIgnoreCase` で重複除去)。
2. **`RedrawGate`/`ShouldDraw`/`UiActivityInProgress` という再描画間引き機構が Phase 7 着手前に既に導入されており、この計画は無視して書かれていた。** `Draw()` は無条件には呼ばれず、`_redrawGate.ShouldDraw(activityInProgress)` が true の tick のみ描画される。ジョブ進捗のスムーズな表示のため `UiActivityInProgress` に `|| JobManager.HasActiveJobs` を追加し、`ShowConfirm`/`ShowListSelect` 等が自前で再描画要求しない点に対応するためダイアログキュー消費後・ジョブ完了検知後に `_redrawGate.Request()` を明示的に呼ぶよう各所に追加した。
3. **`BuildFrame` のステータス行分岐に計画が知らない `_missingEntryMessage` 分岐が存在した。** ジョブサマリはエラー/`_missingEntryMessage` より低優先度・通常ヒントより高優先度として挿入(計画の優先順位表には無い判断)。
4. **`Column.CachedChildren`/`CachedTime` は書き込まれるだけで読み取られない死んだフィールドだった。** 計画の `RefreshPaneNowAsync`(`CachedChildren.Clear()` のみ)は無害だが無意味な no-op になっていたはず。実装セッションが `RefreshCurrentAsync` と同じ「`CreateColumnAsync` で再読込 → カーソル位置/マーク引き継ぎ → 差し替え」方式に書き換えた。
5. **`ExitSearchMode` を `void`→`async Task` に変更した。** 計画通りの fire-and-forget (`_ = RefreshPaneNowAsync(pane)`) だと、ジョブ完了直後に検索モードを抜けたとき、呼び出し元の `HandleSearchKeyAsync` が直後に行う `RebuildRightSideAsync` と競合し `pane.Columns` を破壊しうる実在のバグをレビューで検出。呼び出し元2箇所を `await` に変更して解消。
6. **クリティカルなバグをレビューで検出・修正: `StartCopyOrMoveAsync` にソース側の `"::DRIVES::"` ガードが無かった。** 削除(`RequestDeleteSelected`)には既存のガードがあったが、コピー/移動には計画にもコードにも無かった。ドライブ一覧でカーソルを合わせて `c`/`m` を押すと、`Path.GetFileName("C:")` が `""` を返すためパス結合が `destDir` そのものに潰れ、ドライブ全体の中身が確認なしで宛先直下にコピー/移動(移動時は原本削除)されるおそれがあった。`RequestDeleteSelected` と同じガードを追加して修正済み(コミット `73bb634`)。
7. **未修正・許容した軽微な指摘(コードレビューで検出、影響が小さいため計画の verbatim 設計を優先し変更しなかった):**
   - `TryBuildJobSummary` の 100ms キャッシュはジョブ ID をキーにしていないため、キャッシュ窓内で「最古の実行中ジョブ」が入れ替わると最大 100ms 未満のあいだ古いジョブの情報が表示されうる(次サイクルで自己修復)。
   - `ExitSearchMode`/`ExitLeapModeAsync` の遅延再読込は `onlyDirs` を渡さずペイン全カラムを再読込する(影響カラムのみではない)。機能的には正しいが非効率。
   - 終了確認ダイアログ表示中〜`_shouldExit` セットまでの間に再度 Esc を押すと確認ダイアログが二重に開き得る(実害はないが UX として気になる)。
   - `ShowConfirm` へ渡す `async result => {...}` は async-void 相当(計画で明示的に許容された設計)。`JobManager.CancelAllAndWaitAsync` は現状例外を投げない実装のため今は無害だが、将来その実装が変わった場合は無捕捉例外でプロセスが終了しうる潜在リスクとして記録のみ。
8. **`JobManagerTests` は同一セッション内の複数回実行で一度だけ一過性の失敗(2件、再実行で成功)を観測した。** `ConcurrencyLimit_...`/`CancelAllAndWaitAsync_...` は元々タイミング依存であることがテスト作成時から認識されており(Task 2 コメント参照)、`DisableTestParallelization` の追加はこの計画のスコープ外として見送った。継続的に不安定であれば別途対応を検討すること。
9. **実機での対話的な手動確認(実際に `zrr.exe` を起動して `c`/`m` を押す、進捗バーの滑らかさを目視する、衝突ダイアログを実際に操作する等)はこのセッションでは実施していない。** 自動テスト・ビルド・複数ラウンドのコード/spec レビューでロジックは検証したが、TUI を対話的に駆動する手段がセッションに無かったため、各タスクの「手動確認」チェック項目はコードレビューでの代替確認に留まる。ユーザーによる実機確認を推奨する。
