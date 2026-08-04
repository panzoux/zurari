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

    static async Task<Pane> MakePaneAtAsync(string rootDir)
    {
        var pane = new Pane();
        Column col = await Program.CreateColumnAsync(rootDir);
        await col.LoadingTask!;
        pane.Columns.Add(col);
        return pane;
    }

    [Fact]
    public async Task RebuildRightSide_DoesNotAwaitChildLoad()
    {
        string root = MakeTempDir();
        string sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        for (int i = 0; i < 2000; i++)
            File.WriteAllText(Path.Combine(sub, $"f{i:D5}.txt"), "");
        try
        {
            Pane pane = await MakePaneAtAsync(root);
            pane.Columns[0].Selected = pane.Columns[0].Entries.IndexOf("sub/");
            Assert.True(pane.Columns[0].Selected >= 0);

            await Program.RebuildRightSideAsync(pane, 0);

            // Child column was added, and RebuildRightSideAsync returned while its
            // 2000-entry load is still in flight (old code awaited it → IsLoading false).
            Assert.Equal(2, pane.Columns.Count);
            Assert.True(pane.Columns[1].IsLoading, "RebuildRightSideAsync blocked on child LoadingTask");

            await pane.Columns[1].LoadingTask!;
            Assert.Equal(2000, pane.Columns[1].Entries.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExtendRightSideIfReady_ContinuesChainAfterLoad()
    {
        string root = MakeTempDir();
        string sub = Path.Combine(root, "sub");
        string grand = Path.Combine(sub, "grand");
        Directory.CreateDirectory(grand);
        File.WriteAllText(Path.Combine(grand, "leaf.txt"), "");
        try
        {
            Pane pane = await MakePaneAtAsync(root);
            pane.Columns[0].Selected = pane.Columns[0].Entries.IndexOf("sub/");
            await Program.RebuildRightSideAsync(pane, 0);
            await pane.Columns[1].LoadingTask!;
            pane.Columns[1].Selected = pane.Columns[1].Entries.IndexOf("grand/");

            await Program.ExtendRightSideIfReadyAsync(pane);

            Assert.Equal(3, pane.Columns.Count);
            Assert.EndsWith("grand", pane.Columns[2].Path);
        }
        finally { Directory.Delete(root, true); }
    }
}
