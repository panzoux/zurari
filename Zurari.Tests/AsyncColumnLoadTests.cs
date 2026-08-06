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
    public async Task CreateColumnAsync_InvalidPath_ShowsPlaceholderViaLoader()
    {
        string bogus = Path.Combine(Path.GetTempPath(), "zrr_missing_" + Guid.NewGuid().ToString("N"));
        Column col = await Program.CreateColumnAsync(bogus);
        // Stat check now runs inside the loader; the caller gets a loading column immediately.
        Assert.NotNull(col.LoadingTask);
        await col.LoadingTask!;
        Assert.False(col.IsLoading);
        Assert.Single(col.Entries);
        Assert.Equal("<No file>", col.Entries[0]);
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

    [Fact]
    public async Task MoveDown_DuringRepeat_StartsNoReadAndFlagsPending()
    {
        string root = MakeTempDir();
        Directory.CreateDirectory(Path.Combine(root, "d1"));
        Directory.CreateDirectory(Path.Combine(root, "d2"));
        try
        {
            Pane pane = await MakePaneAtAsync(root);
            pane.Columns[0].Selected = 0;                        // on "d1/"
            pane.LastNavigationTime = DateTime.UtcNow;           // simulate mid-repeat

            Program.MoveDown(pane);                              // now synchronous

            Assert.Equal(1, pane.Columns[0].Selected);           // cursor moved ("d2/")
            Assert.True(pane.NavRebuildPending);
            Assert.Single(pane.Columns);                         // no read started

            // Debounce not elapsed → idle processing does nothing
            await Program.ProcessPendingNavigationAsync(pane);
            Assert.True(pane.NavRebuildPending);
            Assert.Single(pane.Columns);

            // Cursor stable past debounce → idle processing rebuilds
            pane.LastNavigationTime = DateTime.UtcNow.AddMilliseconds(-400);
            await Program.ProcessPendingNavigationAsync(pane);
            Assert.False(pane.NavRebuildPending);
            Assert.Equal(2, pane.Columns.Count);
            Assert.EndsWith("d2", pane.Columns[1].Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MoveDown_OntoFile_PrunesStaleRightColumns()
    {
        string root = MakeTempDir("zzz.txt");                    // sorts after directories
        Directory.CreateDirectory(Path.Combine(root, "d1"));
        try
        {
            Pane pane = await MakePaneAtAsync(root);
            pane.Columns[0].Selected = 0;                        // "d1/"
            await Program.RebuildRightSideAsync(pane, 0);
            await pane.Columns[1].LoadingTask!;
            Assert.Equal(2, pane.Columns.Count);

            Program.MoveDown(pane);                              // onto "zzz.txt"

            Assert.Single(pane.Columns);                         // stale right column pruned synchronously
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RefreshPaneNow_DoesNotAwaitLoad_AndReconcilesMarksAfter()
    {
        string root = MakeTempDir("a.txt", "b.txt");
        try
        {
            Pane pane = await MakePaneAtAsync(root);
            pane.Columns[0].Marked.Add("a.txt");
            pane.Columns[0].Marked.Add("ghost.txt"); // no longer on disk

            await Program.RefreshPaneNowAsync(pane);

            Column refreshed = pane.Columns[0];
            if (refreshed.LoadingTask != null) await refreshed.LoadingTask;
            Program.ProcessPendingMarkReconciles(pane);
            Assert.Contains("a.txt", refreshed.Marked);
            Assert.DoesNotContain("ghost.txt", refreshed.Marked);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ProcessPendingNavigation_WorksForNonFocusedPane()
    {
        string root = MakeTempDir();
        Directory.CreateDirectory(Path.Combine(root, "d1"));
        Directory.CreateDirectory(Path.Combine(root, "d2"));
        try
        {
            Pane pane = await MakePaneAtAsync(root);
            pane.Columns[0].Selected = 0;
            Program.MoveDown(pane);                      // prunes + sets NavRebuildPending
            Assert.True(pane.NavRebuildPending);

            // Simulate: this pane lost focus; idle processing must still handle it.
            pane.LastNavigationTime = DateTime.UtcNow.AddMilliseconds(-400);
            await Program.ProcessPendingNavigationAsync(pane);

            Assert.False(pane.NavRebuildPending);
            Assert.Equal(2, pane.Columns.Count);
            Assert.EndsWith("d2", pane.Columns[1].Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PendingNav_DebounceIsPerPane_OtherPaneActivityDoesNotDefer()
    {
        string root = MakeTempDir();
        Directory.CreateDirectory(Path.Combine(root, "d1"));
        Directory.CreateDirectory(Path.Combine(root, "d2"));
        try
        {
            Pane paneA = await MakePaneAtAsync(root);
            paneA.Columns[0].Selected = 0;
            Program.MoveDown(paneA);                       // pending, timestamps A=now
            Pane paneB = await MakePaneAtAsync(root);
            paneB.LastNavigationTime = DateTime.UtcNow;    // B is actively navigating "now"

            paneA.LastNavigationTime = DateTime.UtcNow.AddMilliseconds(-400); // A idle past debounce

            await Program.ProcessPendingNavigationAsync(paneA);

            // A's rebuild must proceed regardless of B's fresh activity.
            Assert.False(paneA.NavRebuildPending);
            Assert.Equal(2, paneA.Columns.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RebuildRightSide_StopsOnCanonicalPathCycle_WithoutRealSymlink()
    {
        // Simulates a self-referential-symlink cycle (like "/bin/X11 -> .") without needing OS
        // symlink privileges: Directory.Exists("<dir>/.") is true and its FullName resolves back
        // to <dir> itself, so a "./" entry is a real, on-disk stand-in for a self-referential
        // symlink -- both collapse to the same canonical path via ResolveCanonicalPath. This
        // proves the guard mechanism deterministically, independent of OS symlink privilege.
        string root = MakeTempDir();
        string loopDir = Path.Combine(root, "loop");
        Directory.CreateDirectory(loopDir);
        try
        {
            Pane pane = await MakePaneAtAsync(root);
            pane.Columns[0].Selected = pane.Columns[0].Entries.IndexOf("loop/");
            Assert.True(pane.Columns[0].Selected >= 0);

            await Program.RebuildRightSideAsync(pane, 0);
            await pane.Columns[1].LoadingTask!;
            Assert.Equal(2, pane.Columns.Count);
            Assert.Equal(loopDir, pane.Columns[1].Path);

            // Inject a self-referential entry into the child column: "./" nominally differs from
            // loopDir's own path string, but Directory.Exists + FullName both resolve it back to
            // loopDir -- exactly the nominal-vs-canonical mismatch that let /bin/X11 recurse forever.
            pane.Columns[1].Entries = new List<string> { "./" };
            pane.Columns[1].Selected = 0;

            await Program.RebuildRightSideAsync(pane, 1);

            // The guard must recognize "./" canonically resolves to loopDir, already present in
            // the chain at index 1, and refuse to append a duplicate/cyclic column.
            Assert.Equal(2, pane.Columns.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RebuildRightSide_StopsOnRealSelfReferentialSymlink()
    {
        // End-to-end regression for the actual reported bug: a directory symlink pointing back
        // at its own parent (like "/bin/X11 -> .") must not cause unbounded right-side expansion.
        string root = MakeTempDir();
        try
        {
            string linkPath = Path.Combine(root, "selfloop");
            Directory.CreateSymbolicLink(linkPath, root); // selfloop -> root (like /bin/X11 -> .)

            Pane pane = await MakePaneAtAsync(root);       // root's only entry is "selfloop/"
            pane.Columns[0].Selected = pane.Columns[0].Entries.IndexOf("selfloop/");
            Assert.True(pane.Columns[0].Selected >= 0);

            await Program.RebuildRightSideAsync(pane, 0);

            // "selfloop" canonically resolves back to root, which is already the head of the
            // chain -- the guard must refuse to expand into it at all, so no child column is
            // ever appended (this is exactly the /bin/X11 scenario that recursed forever pre-fix).
            Assert.Single(pane.Columns);
        }
        finally { Directory.Delete(root, true); }
    }
}
