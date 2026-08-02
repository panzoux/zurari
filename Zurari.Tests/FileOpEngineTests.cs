using Xunit;

public class FileOpEngineTests
{
    sealed class NullProgress : IFileOpProgress
    {
        public void Report(FileOpProgressInfo info) { }
        public void ReportItemResult(OpLogEntry entry) { }
    }

    // Cancels the token once Report has fired `cancelAfter` times, simulating a user cancelling
    // mid-batch after some items have already succeeded.
    sealed class CancelAfterNProgress : IFileOpProgress
    {
        readonly CancellationTokenSource _cts;
        readonly int _cancelAfter;
        int _count;
        public CancelAfterNProgress(CancellationTokenSource cts, int cancelAfter) { _cts = cts; _cancelAfter = cancelAfter; }
        public void Report(FileOpProgressInfo info)
        {
            _count++;
            if (_count >= _cancelAfter) _cts.Cancel();
        }
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

            // RunBatchAsync no longer throws on cancellation — it returns a partial FileOpResult
            // with WasCancelled=true so callers (JobManager, job list/summary UI) can report an
            // accurate succeeded/skipped/failed breakdown even when a job was cancelled mid-run.
            var result = await FileOpEngine.CopyAsync(new[] { srcFile }, destDir, new NullProgress(), NeverAsked(), cts.Token);

            Assert.True(result.WasCancelled);
            Assert.Equal(0, result.Succeeded);
            Assert.False(File.Exists(Path.Combine(destDir, "big.bin")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopyAsync_CancelledMidBatch_ReturnsPartialSucceededCount()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            string fileA = Path.Combine(srcDir, "a.txt");
            string fileB = Path.Combine(srcDir, "b.txt");
            string fileC = Path.Combine(srcDir, "c.txt");
            File.WriteAllText(fileA, "a");
            File.WriteAllText(fileB, "b");
            File.WriteAllText(fileC, "c");

            using var cts = new CancellationTokenSource();
            // Cancel is requested right after the 1st item's progress is reported, so item 1
            // finishes normally and items 2-3 are never attempted.
            var progress = new CancelAfterNProgress(cts, cancelAfter: 1);

            var result = await FileOpEngine.CopyAsync(new[] { fileA, fileB, fileC }, destDir, progress, NeverAsked(), cts.Token);

            Assert.True(result.WasCancelled);
            Assert.Equal(1, result.Succeeded);
            Assert.True(File.Exists(Path.Combine(destDir, "a.txt")));
            Assert.False(File.Exists(Path.Combine(destDir, "b.txt")));
            Assert.False(File.Exists(Path.Combine(destDir, "c.txt")));
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
    public async Task CopyAsync_NestedConflictRetry_SkipOnSecondAttempt_ReportsSkippedNotSucceeded()
    {
        // Forces the nested-retry path deterministically (no timing race needed): the first
        // onConflict call resolves to Rename targeting a name that itself already exists on
        // disk, which makes the first CreateNew attempt throw IOException and triggers a
        // second (nested) onConflict call. That second call resolves to Skip, which must be
        // reported as Skipped, not Succeeded (regression test for the "OK"/Succeeded mislabel).
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(srcDir, "a.txt"), "new");
            File.WriteAllText(Path.Combine(destDir, "a.txt"), "old");
            File.WriteAllText(Path.Combine(destDir, "a (2).txt"), "colliding");

            int callCount = 0;
            Func<string, Task<ConflictDecision?>> onConflict = _ =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<ConflictDecision?>(new ConflictDecision(ConflictResolution.Rename, "a (2).txt"))
                    : Task.FromResult<ConflictDecision?>(new ConflictDecision(ConflictResolution.Skip));
            };

            var result = await FileOpEngine.CopyAsync(
                new[] { Path.Combine(srcDir, "a.txt") }, destDir, new NullProgress(), onConflict, CancellationToken.None);

            Assert.Equal(2, callCount);
            Assert.Equal(0, result.Succeeded);
            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Failed);
            Assert.Equal("old", File.ReadAllText(Path.Combine(destDir, "a.txt")));
            Assert.Equal("colliding", File.ReadAllText(Path.Combine(destDir, "a (2).txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MoveAsync_NestedConflictRetry_SkipOnSecondAttempt_ReportsSkippedNotSucceeded()
    {
        string root = NewTempDir();
        try
        {
            string srcDir = Path.Combine(root, "src");
            string destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(destDir);
            string srcFile = Path.Combine(srcDir, "a.txt");
            File.WriteAllText(srcFile, "new");
            File.WriteAllText(Path.Combine(destDir, "a.txt"), "old");
            File.WriteAllText(Path.Combine(destDir, "a (2).txt"), "colliding");

            int callCount = 0;
            Func<string, Task<ConflictDecision?>> onConflict = _ =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<ConflictDecision?>(new ConflictDecision(ConflictResolution.Rename, "a (2).txt"))
                    : Task.FromResult<ConflictDecision?>(new ConflictDecision(ConflictResolution.Skip));
            };

            var result = await FileOpEngine.MoveAsync(
                new[] { srcFile }, destDir, new NullProgress(), onConflict, CancellationToken.None);

            Assert.Equal(2, callCount);
            Assert.Equal(0, result.Succeeded);
            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Failed);
            Assert.True(File.Exists(srcFile)); // skipped: source must remain
            Assert.Equal("old", File.ReadAllText(Path.Combine(destDir, "a.txt")));
            Assert.Equal("colliding", File.ReadAllText(Path.Combine(destDir, "a (2).txt")));
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
