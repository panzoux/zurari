using Xunit;

public class AsyncColumnLoadTests
{
    internal static string MakeTempDir(params string[] fileNames)
    {
        string dir = Path.Combine(Path.GetTempPath(), "zrr_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in fileNames)
            File.WriteAllText(Path.Combine(dir, f), "");
        return dir;
    }

    [Fact]
    public async Task CreateColumnAsync_Headless_CompletesWithoutConsole()
    {
        string dir = MakeTempDir("a.txt", "b.txt");
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            Assert.NotNull(col.LoadingTask);
            await col.LoadingTask!;
            Assert.False(col.IsLoading);
            Assert.Equal(2, col.Entries.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CancelReadCts_StopsLoadBeforeCompletion()
    {
        // 2000 files: loader needs several ms (enumerate+sort two phases); Cancel() below
        // runs within microseconds of CreateColumnAsync returning, so the loader cannot
        // plausibly have finished. If cancellation is wired correctly, Entries stays short.
        string dir = Path.Combine(Path.GetTempPath(), "zrr_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 2000; i++)
            File.WriteAllText(Path.Combine(dir, $"f{i:D5}.txt"), "");
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            col.ReadCts!.Cancel();
            await col.LoadingTask!;          // must complete (cancelled), not hang or throw
            Assert.False(col.IsLoading);
            Assert.True(col.Entries.Count < 2000,
                $"expected cancelled read, but all {col.Entries.Count} entries were loaded");
        }
        finally { Directory.Delete(dir, true); }
    }
}
