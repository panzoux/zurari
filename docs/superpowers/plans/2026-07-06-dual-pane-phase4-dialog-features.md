# Phase 4: ダイアログ実証機能(削除・リネーム・ブックマーク・ヒストリ・操作ログ基盤) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ダイアログ基盤(確認/テキスト入力/リスト選択)の上に、削除・リネーム・ブックマーク・ヒストリジャンプの4機能と、これらが記録する操作ログ基盤(`OperationLog`)を実装する。
**Architecture:** 同期実行の4ユーザー操作(`d`/`Delete`, `r`, `b`/`Shift+B`, `h`)を `HandleSpecialKeyAsync` に追加し、それぞれ既存のダイアログ開閉 API(`ShowConfirm`/`ShowTextInput`/`ShowListSelect`)を呼ぶ。削除は Windows では `SHFileOperationW` P/Invoke によるごみ箱送りを既定とし、非 Windows では直接削除にフォールバックする。すべての削除・リネーム結果は `OperationLog`(メモリ1000件リング + `%APPDATA%\zurari\operations.log` 追記、10MB で1世代ローテート)に記録する。ブックマークは `%APPDATA%\zurari\bookmarks.txt` に永続化し、ヒストリはセッション内 `static List<string>`(上限100)で保持する。
**Tech Stack:** C# / .NET 8, xUnit(Zurari.Tests), 自前 TUI(外部 TUI フレームワーク禁止)

---

## 前提条件(開始前に検証)

- [ ] Phase 3 が完了していること。次のコマンドで確認する。
  - `dotnet build Zurari.csproj` → exit 0。
  - `dotnet test Zurari.Tests/Zurari.Tests.csproj` → `Passed!` 全緑。
  - `Program.cs` に `sealed class Pane`、`abstract class DialogState`、`sealed class ConfirmDialogState`、`sealed class TextInputDialogState`、`sealed class ListSelectDialogState`、`static void ShowConfirm(string title, string message, Action<bool?> onResult)`、`static void ShowTextInput(string title, string initialValue, Action<string?> onResult)`、`static void ShowListSelect(string title, IReadOnlyList<string> items, Action<string?> onResult)`、`static async Task HandleDialogKeyAsync(...)` が存在することを `grep` で確認する。
    ```
    grep -n "class Pane\|class DialogState\|class ConfirmDialogState\|class TextInputDialogState\|class ListSelectDialogState\|static void ShowConfirm\|static void ShowTextInput\|static void ShowListSelect\|HandleDialogKeyAsync" Program.cs
    ```
    期待: 全パターンが1件以上ヒットする。
  - メインループの入力分岐が「`State.Dialog != null` → `HandleDialogKeyAsync`」を最優先で見ていることを `Main` 内で確認する(目視)。
  - 3種のダイアログが単独で開閉・確定・キャンセルできることを手動確認済みであること(Phase 3 の完了条件)。

この計画と spec に書かれていない判断が必要になったら、自分で決めずに「未解決事項」に記録してそのステップを保留し、次の独立したタスクに進むか停止すること。

---

## 実装に入る前の設計メモ(このセッションが確認した既存コードとの対応)

- 現状の `Column`/`ScreenState`/ナビゲーション関数は Phase 1 でシグネチャの先頭に `Pane pane` を取る形にリファクタリングされている前提。本計画のコード例では `pane` という変数名でこれを受け取る。単一ペイン運用時の呼び出し元は `FocusedPane`(`State.Panes[State.FocusedPane]`)を渡す。
- 影響カラムの再読込は、Phase 1 以前に存在した `RefreshCurrent()`/`RefreshCurrentAsync()`(アクティブカラムをキャッシュ破棄して `CreateColumnAsync` で作り直し、`Columns[ActiveColumn]` を差し替えたのち `RebuildRightSideAsync` を呼ぶ)と同じパターンを踏襲する。削除・リネームは常に「フォーカスペインのアクティブカラムそのもの」が影響カラムである(操作対象はアクティブカラム内のエントリであり、宛先ディレクトリという概念がない)ため、`RefreshCurrentAsync` 相当のロジックを `Pane` 引数版として呼び出す。
- カーソル追従は既存の `Column.RestoreTo` + `SeekRestoreTo(Column col)` 機構をそのまま使う。`col.RestoreTo = 新しい名前; SeekRestoreTo(col);` を再読込直後に呼べば、`Entries` 内に新名が見つかった位置へ `Selected` が移動する。
- ディレクトリ移動の記録ポイントは `EnterAsync(Pane pane)`、`Parent(Pane pane)`、および本計画で新設する `JumpToPathAsync(Pane pane, string path)` の3箇所。
- `HandleSpecialKeyAsync(Pane pane, ConsoleKeyInfo key)` の `switch (key.Key)` には `ConsoleKey.B`(既存: Ctrl+B = PageUp)が既にあるため、`b`/`Shift+B` はこの既存 case 内に modifiers 分岐を追加する。`d`/`r`/`h` は新規 case を追加する。`ConsoleKey.Delete` も新規 case で `d` と同じ処理を呼ぶ。

---

### Task 1: OpLogEntry / OperationLog の実装(TDD)

**Files:**
- Modify: `Program.cs`
- Test: `Zurari.Tests/OperationLogTests.cs`(新規作成)

- [ ] `Zurari.Tests/OperationLogTests.cs` を新規作成し、以下のテストを書く(すべて `Path.Combine(Path.GetTempPath(), "zurari-oplog-test-" + Guid.NewGuid())` のようなテンポラリディレクトリを各テストで作成・後始末し、`OperationLog` の書き込み先を差し替えられるようにする)。

  `OperationLog` は書き込み先ディレクトリをテストから差し替え可能にする必要があるため、`internal` な差し替えフックを設ける設計とする(下記実装ステップの `LogDirectoryOverride` を参照)。

  ```csharp
  using System;
  using System.Collections.Generic;
  using System.IO;
  using Xunit;

  public class OperationLogTests
  {
      static string NewTempDir()
      {
          string dir = Path.Combine(Path.GetTempPath(), "zurari-oplog-test-" + Guid.NewGuid().ToString("N"));
          Directory.CreateDirectory(dir);
          return dir;
      }

      [Fact]
      public void Append_ThenSnapshot_ReturnsNewestFirst()
      {
          string dir = NewTempDir();
          try
          {
              OperationLog.ResetForTest(dir);
              var e1 = new OpLogEntry(DateTime.UtcNow, null, "DELETE", "OK", "a.txt", "", "");
              var e2 = new OpLogEntry(DateTime.UtcNow, null, "RENAME", "OK", "b.txt", "c.txt", "");
              OperationLog.Append(e1);
              OperationLog.Append(e2);

              var snap = OperationLog.Snapshot();

              Assert.Equal(2, snap.Count);
              Assert.Equal("RENAME", snap[0].Op);   // newest first
              Assert.Equal("DELETE", snap[1].Op);
          }
          finally
          {
              Directory.Delete(dir, true);
          }
      }

      [Fact]
      public void Append_Beyond1000_KeepsRingAt1000_NewestFirst()
      {
          string dir = NewTempDir();
          try
          {
              OperationLog.ResetForTest(dir);
              for (int i = 0; i < 1005; i++)
              {
                  OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "DELETE", "OK", $"file{i}.txt", "", ""));
              }

              var snap = OperationLog.Snapshot();

              Assert.Equal(1000, snap.Count);
              // Newest entry appended was file1004.txt, oldest retained is file5.txt
              Assert.Equal("file1004.txt", snap[0].Source);
              Assert.Equal("file5.txt", snap[999].Source);
          }
          finally
          {
              Directory.Delete(dir, true);
          }
      }

      [Fact]
      public void Append_WritesPlainTextLineToLogFile()
      {
          string dir = NewTempDir();
          try
          {
              OperationLog.ResetForTest(dir);
              OperationLog.Append(new OpLogEntry(DateTime.UtcNow, 3, "COPY", "OK", "src.txt", "dst.txt", "note"));

              string logPath = Path.Combine(dir, "operations.log");
              Assert.True(File.Exists(logPath));
              string content = File.ReadAllText(logPath);
              Assert.Contains("COPY", content);
              Assert.Contains("src.txt", content);
              Assert.Contains("dst.txt", content);
          }
          finally
          {
              Directory.Delete(dir, true);
          }
      }

      [Fact]
      public void Append_RotatesLogFileWhenExceeding10MB()
      {
          string dir = NewTempDir();
          try
          {
              OperationLog.ResetForTest(dir);
              string logPath = Path.Combine(dir, "operations.log");
              string rotatedPath = Path.Combine(dir, "operations.log.1");

              // Pre-fill the log file to just under/over the 10MB threshold directly on disk,
              // then append one more entry and expect a rotation to occur.
              byte[] filler = new byte[10 * 1024 * 1024 + 1];
              File.WriteAllBytes(logPath, filler);

              OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "DELETE", "OK", "trigger.txt", "", ""));

              Assert.True(File.Exists(rotatedPath));
              Assert.True(File.Exists(logPath));
              // New log file should be small again (only the just-appended line), not the 10MB filler.
              var newInfo = new FileInfo(logPath);
              Assert.True(newInfo.Length < 10 * 1024 * 1024);
          }
          finally
          {
              Directory.Delete(dir, true);
          }
      }

      [Fact]
      public void Append_NeverThrows_WhenLogDirectoryIsUnwritable()
      {
          // Point at a path that cannot be created as a directory (a file masquerading as a dir path).
          string dir = NewTempDir();
          string blockedPath = Path.Combine(dir, "blocked-file");
          File.WriteAllText(blockedPath, "x");
          try
          {
              // Use a subpath under the file, which cannot exist as a directory.
              string impossibleDir = Path.Combine(blockedPath, "sub");
              OperationLog.ResetForTest(impossibleDir);

              var ex = Record.Exception(() =>
                  OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "DELETE", "OK", "x.txt", "", "")));

              Assert.Null(ex);
              // Memory ring must still work even if file write silently failed.
              Assert.Single(OperationLog.Snapshot());
          }
          finally
          {
              Directory.Delete(dir, true);
          }
      }
  }
  ```

- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter OperationLogTests` を実行し、コンパイルエラー(`OpLogEntry`/`OperationLog` 未定義)で失敗することを確認する。

- [ ] `Program.cs` に `OpLogEntry` レコードと `OperationLog` クラスを追加する。挿入位置: `sealed class ScreenState` の直後(既存の状態クラス群と同じ並びに置く)。

  ```csharp
  sealed record OpLogEntry(DateTime Timestamp, int? JobId, string Op, string Result, string Source, string Dest, string Message);

  static class OperationLog
  {
      const int RingLimit = 1000;
      const long MaxLogSizeBytes = 10L * 1024 * 1024;

      static readonly object _lock = new object();
      static readonly LinkedList<OpLogEntry> _ring = new LinkedList<OpLogEntry>(); // newest at First

      // Overridable only from tests (InternalsVisibleTo covers Zurari.Tests).
      static string? _logDirectoryOverride;

      static string LogDirectory
      {
          get
          {
              if (_logDirectoryOverride != null) return _logDirectoryOverride;
              return Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                  "zurari");
          }
      }

      static string LogFilePath => Path.Combine(LogDirectory, "operations.log");
      static string RotatedFilePath => Path.Combine(LogDirectory, "operations.log.1");

      internal static void ResetForTest(string logDirectory)
      {
          lock (_lock)
          {
              _logDirectoryOverride = logDirectory;
              _ring.Clear();
          }
      }

      public static void Append(OpLogEntry e)
      {
          lock (_lock)
          {
              _ring.AddFirst(e);
              while (_ring.Count > RingLimit)
                  _ring.RemoveLast();

              TryAppendToFile(e);
          }
      }

      static void TryAppendToFile(OpLogEntry e)
      {
          try
          {
              Directory.CreateDirectory(LogDirectory);

              RotateIfNeeded();

              string line = string.Join('\t',
                  e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                  e.JobId?.ToString() ?? "-",
                  e.Op,
                  e.Result,
                  e.Source,
                  e.Dest,
                  e.Message)
                  .Replace('\n', ' ')
                  .Replace('\r', ' ');

              using var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
              using var writer = new StreamWriter(stream, Encoding.UTF8);
              writer.WriteLine(line);
          }
          catch
          {
              // Logging must never crash the app. Silently give up on the file sink;
              // the in-memory ring already has the entry.
          }
      }

      static void RotateIfNeeded()
      {
          try
          {
              var info = new FileInfo(LogFilePath);
              if (!info.Exists || info.Length < MaxLogSizeBytes) return;

              if (File.Exists(RotatedFilePath))
                  File.Delete(RotatedFilePath);
              File.Move(LogFilePath, RotatedFilePath);
          }
          catch
          {
              // Best-effort rotation only.
          }
      }

      public static IReadOnlyList<OpLogEntry> Snapshot()
      {
          lock (_lock)
          {
              return new List<OpLogEntry>(_ring); // already newest-first due to AddFirst
          }
      }
  }
  ```

  補足(ダブル NUL 終端の罠と直接関係する注意ではないが、書き込みの落とし穴): `FileMode.Append` はプロセス起動ごとに新規 `FileStream` を開いて追記するため複数プロセス間の排他は保証しないが、zurari は単一プロセス・単一 `_lock` 内で完結するため単一プロセス内の競合は `_lock` で防げる。`using` によるオープン/クローズを毎回行うのは頻度が低い(削除・リネームは同期操作でユーザー操作頻度)ため許容する。

- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj --filter OperationLogTests` を実行し、5件すべて Passed になることを確認する。
- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] コミットする。
  ```
  git add Program.cs Zurari.Tests/OperationLogTests.cs
  git commit -m "feat: add OperationLog with 1000-entry ring buffer and file rotation"
  ```

---

### Task 2: RecycleBin(SHFileOperationW P/Invoke)の実装

**Files:**
- Modify: `Program.cs`

- [ ] `Program.cs` に `RecycleBin` 静的クラスを追加する。挿入位置: `OperationLog` の直後。

  ```csharp
  static class RecycleBin
  {
      const uint FO_DELETE = 0x0003;
      const ushort FOF_ALLOWUNDO = 0x0040;
      const ushort FOF_NOCONFIRMATION = 0x0010;
      const ushort FOF_SILENT = 0x0004;

      [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
      struct SHFILEOPSTRUCTW
      {
          public IntPtr hwnd;
          public uint wFunc;
          [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
          public string pFrom;
          [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
          public string? pTo;
          public ushort fFlags;
          [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
          public bool fAnyOperationsAborted;
          public IntPtr hNameMappings;
          [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
          public string? lpszProgressTitle;
      }

      [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
      static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

      // SHFileOperationW requires pFrom to be a list of paths terminated by TWO null characters
      // (a single trailing '\0' only terminates the last path in the list; Windows scans for the
      // *second* consecutive null to know the whole multi-string buffer has ended). Forgetting the
      // second '\0' causes SHFileOperationW to read past the buffer or silently fail.
      public static bool TrySend(string path, out string error)
      {
          error = "";

          if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
              System.Runtime.InteropServices.OSPlatform.Windows))
          {
              error = "not supported on this platform";
              return false;
          }

          try
          {
              var fileOp = new SHFILEOPSTRUCTW
              {
                  hwnd = IntPtr.Zero,
                  wFunc = FO_DELETE,
                  pFrom = path + "\0\0", // double-null terminated, single-entry list
                  pTo = null,
                  fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT),
                  fAnyOperationsAborted = false,
                  hNameMappings = IntPtr.Zero,
                  lpszProgressTitle = null,
              };

              int result = SHFileOperationW(ref fileOp);
              if (result != 0)
              {
                  error = $"SHFileOperationW failed (code {result})";
                  return false;
              }
              if (fileOp.fAnyOperationsAborted)
              {
                  error = "operation aborted";
                  return false;
              }
              return true;
          }
          catch (Exception ex)
          {
              error = ex.Message;
              return false;
          }
      }
  }
  ```

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する(Windows 実機ビルド。P/Invoke 宣言のみなので非 Windows でもコンパイルは通る)。
- [ ] コミットする。
  ```
  git add Program.cs
  git commit -m "feat: add RecycleBin.TrySend via SHFileOperationW P/Invoke"
  ```

---

### Task 3: 削除機能(`d` / `Delete`)

**Files:**
- Modify: `Program.cs`

- [ ] `HandleSpecialKeyAsync(Pane pane, ConsoleKeyInfo key)` の `switch (key.Key)` に以下の2 case を追加する(`case ConsoleKey.Backspace:` の直後、`case ConsoleKey.V:` の直前に挿入する)。

  ```csharp
              case ConsoleKey.Delete:
                  RequestDeleteSelected(pane);
                  break;
  ```

  さらに `default:` 節の `if (key.KeyChar == '/' ...)` チェーンに `else if` として追加する(`key.KeyChar == '\\'` の分岐の後に追加):

  ```csharp
              default:
                  if (key.KeyChar == '/' && key.Modifiers == ConsoleModifiers.None)
                      EnterSearchMode(pane);
                  else if (key.KeyChar == '\\' && key.Modifiers == ConsoleModifiers.None)
                      await ShowContextMenuAsync(pane);
                  else if (key.KeyChar == 'd' && key.Modifiers == ConsoleModifiers.None)
                      RequestDeleteSelected(pane);
                  else if (key.KeyChar == 'r' && key.Modifiers == ConsoleModifiers.None)
                      RequestRenameSelected(pane);
                  else if (key.KeyChar == 'h' && key.Modifiers == ConsoleModifiers.None)
                      RequestHistoryJump(pane);
                  break;
  ```

  (`b`/`Shift+B` は Task 5 で `case ConsoleKey.B:` に追加するため、ここでは触れない。`r` の実処理は Task 4、`h` は Task 6 で実装する関数を呼ぶだけここで先に配線しておく。)

- [ ] `RequestDeleteSelected(Pane pane)` を追加する。挿入位置: `EnterAsync` の直前(削除・リネームなど「選択中エントリに対する操作」をひとまとめに配置する)。

  ```csharp
  static bool SelectedIsDeletable(Pane pane, out Column col, out string name)
  {
      col = pane.Columns[pane.ActiveColumn];
      name = "";
      if (col.Path == "::DRIVES::") return false;
      if (col.Entries.Count == 0) return false;
      if (col.Selected < 0 || col.Selected >= col.Entries.Count) return false;
      name = col.Entries[col.Selected];
      if (name == "<No file>") return false;
      return true;
  }

  static void RequestDeleteSelected(Pane pane)
  {
      if (!SelectedIsDeletable(pane, out Column col, out string name))
          return;

      string displayName = name.TrimEnd('/');
      ShowConfirm(
          "削除",
          $"{displayName} を削除しますか?(ごみ箱へ)",
          async result =>
          {
              if (result != true) return;
              await ExecuteDeleteAsync(pane, col, name);
          });
  }

  static async Task ExecuteDeleteAsync(Pane pane, Column col, string name)
  {
      string fullPath = Path.Combine(col.Path, name.TrimEnd('/'));
      bool isDir = name.EndsWith("/");
      string opName = "DELETE";
      string result;
      string message = "";

      bool sentToRecycleBin = false;
      if (IsWindows())
      {
          sentToRecycleBin = RecycleBin.TrySend(fullPath, out string rbError);
          if (!sentToRecycleBin)
              message = rbError;
      }

      if (sentToRecycleBin)
      {
          result = "OK";
          _lastErrorMessage = null;
      }
      else
      {
          try
          {
              if (isDir)
                  Directory.Delete(fullPath, recursive: true);
              else
                  File.Delete(fullPath);
              result = "OK";
              _lastErrorMessage = null;
          }
          catch (Exception ex)
          {
              result = "FAIL";
              message = ex.Message;
              _lastErrorMessage = $"削除失敗: {ex.Message}";
          }
      }

      OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, opName, result, fullPath, "", message));

      await RefreshCurrentAsync(pane);
  }
  ```

  - `RefreshCurrentAsync(Pane pane)` は Phase 1 リファクタリング後の既存関数(旧 `RefreshCurrentAsync()` を `pane` 引数化したもの)をそのまま呼ぶ。存在しない場合(まだシグネチャが変わっていない)は「未解決事項」に記録して停止する。

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 手動確認: テスト用ディレクトリで適当なファイルにカーソルを合わせ `d` を押す。確認ダイアログ「`<ファイル名>` を削除しますか?(ごみ箱へ)」が中央に表示されることを確認する。`Y` で確定し、Windows のごみ箱(エクスプローラーの「ごみ箱」アイコン)にファイルが移動していること、カラムの一覧からファイルが消えること、ステータス行にエラーが出ないことを確認する。`Delete` キーでも同じ動作になることを確認する。ドライブ一覧(`::DRIVES::`)にカーソルがある状態で `d` を押しても何も起きない(ダイアログが開かない)ことを確認する。
- [ ] コミットする。
  ```
  git add Program.cs
  git commit -m "feat: add delete (d/Delete) with recycle-bin confirmation dialog"
  ```

---

### Task 4: リネーム機能(`r`)

**Files:**
- Modify: `Program.cs`

- [ ] `RequestRenameSelected(Pane pane)` を追加する。挿入位置: `RequestDeleteSelected`/`ExecuteDeleteAsync` の直後。

  ```csharp
  static void RequestRenameSelected(Pane pane)
  {
      if (!SelectedIsDeletable(pane, out Column col, out string name))
          return;

      bool isDir = name.EndsWith("/");
      string currentName = isDir ? name.TrimEnd('/') : name;

      ShowTextInput(
          "リネーム",
          currentName,
          async newNameResult =>
          {
              if (newNameResult == null) return;
              string newName = newNameResult.Trim();
              if (newName.Length == 0 || newName == currentName) return;
              await ExecuteRenameAsync(pane, col, name, currentName, newName, isDir);
          });
  }

  static async Task ExecuteRenameAsync(Pane pane, Column col, string originalEntry, string currentName, string newName, bool isDir)
  {
      string oldFullPath = Path.Combine(col.Path, currentName);
      string newFullPath = Path.Combine(col.Path, newName);
      string result;
      string message = "";

      try
      {
          if (isDir)
              Directory.Move(oldFullPath, newFullPath);
          else
              File.Move(oldFullPath, newFullPath);
          result = "OK";
          _lastErrorMessage = null;
      }
      catch (Exception ex)
      {
          result = "FAIL";
          message = ex.Message;
          _lastErrorMessage = $"リネーム失敗: {ex.Message}";
      }

      OperationLog.Append(new OpLogEntry(DateTime.UtcNow, null, "RENAME", result, oldFullPath, newFullPath, message));

      if (result == "OK")
      {
          col.RestoreTo = isDir ? newName + "/" : newName;
      }

      await RefreshCurrentAsync(pane);
  }
  ```

  - カーソル追従: `RefreshCurrentAsync(pane)` 内部で `CreateColumnAsync` が新しい `Column` インスタンスを作って `Columns[pane.ActiveColumn]` を差し替える実装になっている場合、`col.RestoreTo` に設定しても新インスタンスには伝播しない。既存の `ApplyCursorMemory`/`SeekRestoreTo` の呼び出し箇所を確認し、`RefreshCurrentAsync` が新しい `Column` に対して `RestoreTo` を引き継ぐ処理を行っているかを検証すること。もし引き継がれない実装であれば、`ExecuteRenameAsync` は `RefreshCurrentAsync` を呼ぶ代わりに次の手順を直接実行する形に書き換える。
    ```csharp
    Column refreshed = await CreateColumnAsync(col.Path, CancellationToken.None);
    if (refreshed.LoadingTask != null) await refreshed.LoadingTask;
    refreshed.RestoreTo = isDir ? newName + "/" : newName;
    SeekRestoreTo(refreshed);
    pane.Columns[pane.ActiveColumn] = refreshed;
    await RebuildRightSideAsync(pane, pane.ActiveColumn);
    ```
    どちらの経路になるかは Phase 1/3 のリファクタリング結果に依存するため、実装時に `RefreshCurrentAsync` の定義を読んで確定させること。判断がつかない場合は「未解決事項」に記録して停止する。

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 手動確認: ファイルにカーソルを合わせ `r` を押す。テキスト入力ダイアログが開き、初期値が現在のファイル名(拡張子込み、ディレクトリなら末尾 `/` なしの名前)になっていることを確認する。名前を変更して `Enter`。一覧が再読込され、カーソルが新しい名前の行に移動していることを確認する。日本語ファイル名(例: `テスト.txt`)へのリネームも文字化けなく行えることを確認する。`Esc` でキャンセルした場合は何も変更されないことを確認する。
- [ ] コミットする。
  ```
  git add Program.cs
  git commit -m "feat: add rename (r) with text-input dialog and cursor follow"
  ```

---

### Task 5: JumpToPathAsync の新設(ブックマーク・ヒストリ共通基盤)

**Files:**
- Modify: `Program.cs`

- [ ] `JumpToPathAsync(Pane pane, string path)` を追加する。挿入位置: `RebuildRightSideAsync` の直後。この関数は指定パスに向けて `pane.Columns` を先頭カラム(ルート)から作り直す。既存の `GetRootPath()`/`CreateColumnAsync`/`RebuildRightSideAsync` のパターンをそのまま流用する。

  ```csharp
  static async Task JumpToPathAsync(Pane pane, string path)
  {
      string fullPath;
      try
      {
          fullPath = Path.GetFullPath(path);
      }
      catch
      {
          _lastErrorMessage = $"無効なパス: {path}";
          return;
      }

      if (!Directory.Exists(fullPath))
      {
          _lastErrorMessage = $"パスが見つかりません: {path}";
          return;
      }

      // Cancel any in-flight loads on the existing column chain before discarding it.
      foreach (Column existing in pane.Columns)
          existing.ReadCts?.Cancel();

      // Split the target path into segments, e.g. "C:\Users\me\docs" -> ["C:\", "Users", "me", "docs"].
      var segments = new List<string>();
      string root = Path.GetPathRoot(fullPath) ?? "";
      string remainder = fullPath.Substring(root.Length);
      foreach (string part in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
      {
          if (part.Length > 0) segments.Add(part);
      }

      pane.Columns.Clear();

      string rootMarker = GetRootPath();
      Column rootColumn = await CreateColumnAsync(rootMarker, CancellationToken.None);
      if (rootColumn.LoadingTask != null) await rootColumn.LoadingTask;
      pane.Columns.Add(rootColumn);

      // Select the drive (Windows) or first segment inside the root column.
      string accumulatedPath;
      if (IsWindows())
      {
          string driveEntry = root.TrimEnd('\\') + "/";
          int driveIndex = rootColumn.Entries.IndexOf(driveEntry);
          if (driveIndex < 0)
          {
              _lastErrorMessage = $"パスが見つかりません: {path}";
              return;
          }
          rootColumn.Selected = driveIndex;
          accumulatedPath = root; // e.g. "C:\"
      }
      else
      {
          accumulatedPath = "/";
      }

      pane.ActiveColumn = 0;

      foreach (string segment in segments)
      {
          Column current = pane.Columns[pane.ActiveColumn];
          string entryName = segment + "/";
          int idx = current.Entries.IndexOf(entryName);
          if (idx < 0)
          {
              _lastErrorMessage = $"パスが見つかりません: {path}";
              return;
          }
          current.Selected = idx;

          accumulatedPath = Path.Combine(accumulatedPath, segment);
          Column next = await CreateColumnAsync(accumulatedPath, CancellationToken.None);
          if (next.LoadingTask != null) await next.LoadingTask;

          while (pane.Columns.Count > pane.ActiveColumn + 1)
              pane.Columns.RemoveAt(pane.Columns.Count - 1);

          pane.Columns.Add(next);
          pane.ActiveColumn++;
      }

      UpdateHorizontalScroll(pane);
      _lastErrorMessage = null;
      RecordHistoryVisit(fullPath);
  }
  ```

  - `RecordHistoryVisit` は Task 6 で定義する関数。ここで先に呼び出しを配線する(Task 6 のコミットより前にビルドが通らなくなるため、Task 5 と Task 6 は同一コミットにまとめてよい。ただし本計画のステップ粒度は分けて記述する)。
  - `UpdateHorizontalScroll` が `Pane` 引数を取る形にリファクタリング済みであることを前提とする(Phase 1)。異なる場合は「未解決事項」に記録する。

- [ ] `dotnet build Zurari.csproj` を実行する。この時点では `RecordHistoryVisit` が未定義でビルドが失敗する。Task 6 のステップと合わせて解消する(下記参照)。

---

### Task 6: ヒストリ機構(`h`)

**Files:**
- Modify: `Program.cs`

- [ ] `static readonly List<string> _history = new();` を static フィールド群(`_lastErrorMessage` 等の近く)に追加する。

  ```csharp
  static readonly List<string> _history = new();
  const int HistoryLimit = 100;
  ```

- [ ] `RecordHistoryVisit(string fullPath)` を追加する。挿入位置: `JumpToPathAsync` の直後。

  ```csharp
  static void RecordHistoryVisit(string fullPath)
  {
      int existingIndex = _history.FindIndex(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
      if (existingIndex >= 0)
          _history.RemoveAt(existingIndex);

      _history.Add(fullPath);

      while (_history.Count > HistoryLimit)
          _history.RemoveAt(0);
  }
  ```

- [ ] `EnterAsync(Pane pane)` の末尾(`await RebuildRightSideAsync(pane, pane.ActiveColumn - 1);` の後)に `RecordHistoryVisit(GetCurrentFullPath(pane));` を追加する。ただし `EnterAsync` はディレクトリに入った**後**の状態でフルパスを取得する必要があるため、`pane.ActiveColumn` が進んだ後・カラム構築後の位置に置く。既存の `EnterAsync` の実装を読み、「新しいアクティブカラムのディレクトリパス」を指す式(通常は `pane.Columns[pane.ActiveColumn - 1].Path` に対応する `GetSelectedDirectory` で得たパス、または新しく追加された最後のカラムの `Path`)を特定して渡すこと。目安のコード:

  ```csharp
  static async Task EnterAsync(Pane pane)
  {
      // ...(既存の入室処理)...

      await RebuildRightSideAsync(pane, pane.ActiveColumn - 1);

      // Record the directory we just entered (the column at ActiveColumn - 1's selected entry).
      Column entered = pane.Columns[pane.ActiveColumn - 1];
      if (entered.Selected >= 0 && entered.Selected < entered.Entries.Count)
      {
          string enteredPath = GetSelectedDirectory(pane, pane.ActiveColumn - 1) ?? "";
          if (enteredPath.Length > 0)
              RecordHistoryVisit(enteredPath);
      }
  }
  ```

  実際の変数名・既存コードの形と食い違う場合は、既存の `EnterAsync` を優先し、そのロジックに沿う形で `RecordHistoryVisit` 呼び出しを挿入すること(擬似コードではなく実際の関数を読んで確定させる)。

- [ ] `Parent(Pane pane)` の末尾(`if (State.Preview.IsVisible) StartPreviewLoad();` 相当の後)に以下を追加する。

  ```csharp
  RecordHistoryVisit(pane.Columns[pane.ActiveColumn].Path);
  ```

- [ ] `RequestHistoryJump(Pane pane)` を追加する。挿入位置: `RecordHistoryVisit` の直後。

  ```csharp
  static void RequestHistoryJump(Pane pane)
  {
      if (_history.Count == 0)
      {
          _lastErrorMessage = "ヒストリはまだありません";
          return;
      }

      // Newest-first for display.
      var items = new List<string>(_history);
      items.Reverse();

      ShowListSelect(
          "ヒストリ",
          items,
          async selected =>
          {
              if (selected == null) return;
              await JumpToPathAsync(pane, selected);
          });
  }
  ```

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する(Task 5 で保留していたビルドエラーもここで解消される)。
- [ ] 手動確認: いくつかのディレクトリを行き来したあと `h` を押す。リスト選択ダイアログが新しい順(最後に訪れたディレクトリが最上位)で開くことを確認する。項目を選んで `Enter` すると、その場所へジャンプしカラムが正しく再構築されることを確認する。存在しないパス(選択後に外部から削除された場合を模してテスト時のみ発生しうる)では `_lastErrorMessage` にエラーが表示されジャンプが行われないことを確認する。
- [ ] コミットする。
  ```
  git add Program.cs
  git commit -m "feat: add JumpToPathAsync and session history (h) with list-select dialog"
  ```

---

### Task 7: ブックマーク機構(`b` = ジャンプ, `Shift+B` = 追加)

**Files:**
- Modify: `Program.cs`

- [ ] `Bookmarks` 静的クラスを追加する。挿入位置: `RecycleBin` の直後。

  ```csharp
  static class Bookmarks
  {
      static string? _pathOverride;

      internal static void ResetForTest(string filePath)
      {
          _pathOverride = filePath;
      }

      static string FilePath
      {
          get
          {
              if (_pathOverride != null) return _pathOverride;
              string dir = Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                  "zurari");
              return Path.Combine(dir, "bookmarks.txt");
          }
      }

      public static List<string> Load()
      {
          var result = new List<string>();
          try
          {
              if (!File.Exists(FilePath)) return result;
              foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
              {
                  string trimmed = line.Trim();
                  if (trimmed.Length > 0)
                      result.Add(trimmed);
              }
          }
          catch
          {
              // Ignore unreadable bookmarks file; treat as empty.
          }
          return result;
      }

      public static bool Add(string path)
      {
          var existing = Load();
          foreach (string p in existing)
          {
              if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                  return false; // already present
          }

          try
          {
              string dir = Path.GetDirectoryName(FilePath) ?? ".";
              Directory.CreateDirectory(dir);
              existing.Add(path);
              File.WriteAllLines(FilePath, existing, Encoding.UTF8);
              return true;
          }
          catch
          {
              return false;
          }
      }
  }
  ```

- [ ] `HandleSpecialKeyAsync` 内の既存 `case ConsoleKey.B:` を次の内容に置き換える(Ctrl+B の既存動作を保ったまま、無modifier=ジャンプ、Shift=追加を追加する)。

  ```csharp
              case ConsoleKey.B:
                  if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                  {
                      NavigationHelper.PageUp(pane.Columns[pane.ActiveColumn], Console.WindowHeight - 3);
                      UpdateHorizontalScroll(pane);
                      await RebuildRightSideAsync(pane, pane.ActiveColumn);
                      if (pane.Preview.IsVisible) StartPreviewLoad(pane);
                  }
                  else if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                  {
                      RequestBookmarkAdd(pane);
                  }
                  else if (key.Modifiers == ConsoleModifiers.None)
                  {
                      RequestBookmarkJump(pane);
                  }
                  break;
  ```

  既存コード中の `Columns[State.ActiveColumn]` / `UpdateHorizontalScroll()` / `RebuildRightSideAsync(State.ActiveColumn)` / `StartPreviewLoad()` は、Phase 1 のリファクタリングで `pane` 引数版に変わっている前提でそのシグネチャに合わせて書き換えること(既存の他 case の書き方と揃える)。

- [ ] `RequestBookmarkAdd(Pane pane)` と `RequestBookmarkJump(Pane pane)` を追加する。挿入位置: `Bookmarks` クラスの直後、または `RequestHistoryJump` の直後(ユーザー操作系関数のまとまりに置く)。

  ```csharp
  static void RequestBookmarkAdd(Pane pane)
  {
      string currentDir = pane.Columns[pane.ActiveColumn].Path;
      if (currentDir == "::DRIVES::")
      {
          _lastErrorMessage = "ドライブ一覧はブックマークできません";
          return;
      }

      bool added = Bookmarks.Add(currentDir);
      _lastErrorMessage = added ? $"ブックマークに追加: {currentDir}" : $"既に登録済み: {currentDir}";
  }

  static void RequestBookmarkJump(Pane pane)
  {
      var items = Bookmarks.Load();
      if (items.Count == 0)
      {
          _lastErrorMessage = "ブックマークはまだありません";
          return;
      }

      ShowListSelect(
          "ブックマーク",
          items,
          async selected =>
          {
              if (selected == null) return;
              await JumpToPathAsync(pane, selected);
          });
  }
  ```

  - 「重複追加しない」は `Bookmarks.Add` 内の完全一致チェック(`OrdinalIgnoreCase`)で担保する。
  - `_lastErrorMessage` はエラー以外の one-shot 通知にも既存コードで使われている前提(`LaunchFileAsync` 等で成功時に `null` を代入するのみで成功メッセージは出していない実例はあるが、本関数では追加成功を利用者に知らせる必要があるため one-shot メッセージとして流用する)。もしこの用途が spec の想定と異なると判断される場合は「未解決事項」に記録する。

- [ ] `dotnet build Zurari.csproj` で exit 0 を確認する。
- [ ] 手動確認: あるディレクトリで `Shift+B` を押す。ステータス行に「ブックマークに追加: <パス>」が表示されることを確認する。同じディレクトリでもう一度 `Shift+B` を押すと「既に登録済み」と表示され `%APPDATA%\zurari\bookmarks.txt` の行数が増えないことを確認する。`b`(無modifier)を押すとブックマーク一覧がリスト選択ダイアログで開き、選択してジャンプできることを確認する。日本語パス(例: `C:\Users\<name>\デスクトップ`)を含むブックマークが文字化けなく保存・表示されることを確認する。
- [ ] コミットする。
  ```
  git add Program.cs
  git commit -m "feat: add bookmark add (Shift+B) and jump (b) persisted to bookmarks.txt"
  ```

---

### Task 8: 削除・リネームの操作ログ記録の最終確認と統合テスト

**Files:**
- Modify: `Program.cs`(必要な場合のみ、Task 3/4 の実装に漏れがあれば補完)
- Test: `Zurari.Tests/OperationLogTests.cs`(既存ファイルに追記)

- [ ] `OperationLogTests.cs` に、実際の削除・リネームフローが `OperationLog` に正しいエントリを残すことを検証する統合テストではなく、`OpLogEntry` のフィールド組み立てが Task 3/4 のコードと一致することを確認する軽量テストを追加する(`ExecuteDeleteAsync`/`ExecuteRenameAsync` はコンソール UI・ダイアログに依存するため直接呼び出すユニットテストは組まない。ログの構造自体は Task 1 のテストで担保済み)。このタスクは新規テストを追加せず、Task 1〜7 のテストがすべて緑であることの再確認のみで完了する。

  ```
  dotnet test Zurari.Tests/Zurari.Tests.csproj
  ```
  期待: 既存テスト + `OperationLogTests`(5件)がすべて Passed。

- [ ] 手動確認(統合): 適当なテストディレクトリで次を順に行い、`%APPDATA%\zurari\operations.log` の末尾を確認する。
  1. ファイルを1つ削除する(`d` → `Y`)。
  2. 別のファイルをリネームする(`r` → 新名 → `Enter`)。
  3. `%APPDATA%\zurari\operations.log` を開き、`DELETE`行と`RENAME`行がそれぞれ1行ずつ、タブ区切りでタイムスタンプ・`-`(JobId無し)・操作種別・`OK`・パスが記録されていることを確認する。

- [ ] コミットする(このタスクで `Program.cs` に変更がなければコミット不要。テスト実行結果の確認のみで完了する場合はスキップしてよい)。

---

## 完了条件チェックリスト

- [ ] `OpLogEntry` / `OperationLog`(メモリ1000件リング + ファイル追記 + 10MBローテート1世代)が実装され、`OperationLogTests.cs` の5テストが緑。
- [ ] `RecycleBin.TrySend` が `SHFileOperationW` P/Invoke で実装され、Windows でごみ箱送りが実機動作する。非 Windows では `false` を返し直接削除にフォールバックする分岐が実装されている。
- [ ] `d` / `Delete` キーで確認ダイアログ→ごみ箱送り(または直接削除フォールバック)→ `OperationLog` 記録→フォーカスペインの影響カラム(=アクティブカラム)再読込が動作する。ドライブ一覧・空カラムでは無効化される。
- [ ] `r` キーでテキスト入力ダイアログ(初期値=現在名、ディレクトリは末尾 `/` を外した名前)→ `File.Move`/`Directory.Move` → `OperationLog` 記録→再読込→カーソルが新名に追従する。
- [ ] `b` でブックマーク一覧からのジャンプ、`Shift+B` で現在ディレクトリの追加(重複は追加しない)が `%APPDATA%\zurari\bookmarks.txt`(UTF-8, 1行1パス)に対して動作する。
- [ ] `h` でセッション内ヒストリ(上限100、重複は末尾へ)からのジャンプがリスト選択ダイアログ(新しい順)で動作する。
- [ ] `JumpToPathAsync(Pane pane, string path)` が新設され、ブックマーク・ヒストリ双方から共通利用されている。存在しないパスは `_lastErrorMessage` にセットされジャンプは行われない。
- [ ] `HandleSpecialKeyAsync` への `d`/`Delete`/`r`/`h`/`b`/`Shift+B` の配線が、既存の `default` 節・`case ConsoleKey.B:` の構造を壊さずに追加されている。
- [ ] `dotnet build Zurari.csproj` が exit 0。
- [ ] `dotnet test Zurari.Tests/Zurari.Tests.csproj` が全緑。
- [ ] 削除・リネーム・ブックマーク・ヒストリジャンプの4機能が実機(Windows)で動作確認済み(日本語ファイル名・パスを含む)。

## 未解決事項

- **worktree のベースが stale だった件**: `EnterWorktree` のデフォルト `baseRef` は `origin/<default-branch>` から分岐する設定だったが、`origin/main` はローカル `main`(571ff91, Phase 3 マージ済み)より大きく遅れており(d33ccac = Phase 1 完了時点)、そのまま作業するとPhase 2/3 の成果(ダイアログ基盤含む)が全て失われた状態で前提条件チェックに失敗するところだった。対応: worktree ブランチに未コミット作業がないことを確認した上で `git reset --hard 571ff91`(ローカル main の最新)を実行し、正しい基点で作業を再開した。
- **Task 3 の `default:` 節への `r`/`h` 事前配線について**: 計画本文のコード例では Task 3 の時点で `d`/`r`/`h` の3つを `default:` 節にまとめて配線し、`r`(`RequestRenameSelected`)は Task 4、`h`(`RequestHistoryJump`)は Task 6 で実装する想定になっていた。しかし Task 3 自体のチェックリストに「`dotnet build Zurari.csproj` で exit 0 を確認する」という無条件の検証ステップがあり、未実装の関数を呼ぶコードを含めるとこの検証が失敗する。計画は Task 5/Task 6 間の一時的なビルド不整合は明示的に許容している(「同一コミットにまとめてよい」)が、Task 3 についてはそのような例外の記述がない。矛盾を自己判断で押し切らず、ここに記録した上で次の対応を取った: Task 3 では `d` のみを `default:` 節に配線し、`r` は Task 4 実装時に、`h` は Task 6 実装時にそれぞれ配線を追加する(各タスクの build 検証を常に green に保つ)。挙動・最終的な配線内容は計画の意図(3キーとも `default:` 節から呼ばれる)と一致しており、コミットの割り方のみが計画のコード例と異なる。
