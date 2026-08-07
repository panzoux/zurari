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

    [Fact]
    public async Task RebuildRightSide_StopsOnMultiHopAncestorSymlink()
    {
        // Deeper variant of the /bin/X11 case: a\b\c\link_a -> a, where the symlink points at
        // an ancestor two levels up (not its own immediate parent). The chain a -> b -> c is
        // built up through real, non-symlinked directories first, so the guard must find the
        // cycle by canonical path against the WHOLE existing column chain, not just the
        // immediately preceding column.
        string a = MakeTempDir();
        try
        {
            string b = Path.Combine(a, "b");
            string c = Path.Combine(b, "c");
            string d = Path.Combine(c, "d");
            Directory.CreateDirectory(d);
            string linkPath = Path.Combine(c, "link_a");
            Directory.CreateSymbolicLink(linkPath, a); // c/link_a -> a (two levels up, not self/parent)

            Pane pane = await MakePaneAtAsync(a);      // pane.Columns[0] = a, only entry "b/"
            pane.Columns[0].Selected = pane.Columns[0].Entries.IndexOf("b/");
            Assert.True(pane.Columns[0].Selected >= 0);

            await Program.RebuildRightSideAsync(pane, 0);
            await pane.Columns[1].LoadingTask!;         // pane.Columns[1] = b, only entry "c/"
            pane.Columns[1].Selected = pane.Columns[1].Entries.IndexOf("c/");
            Assert.True(pane.Columns[1].Selected >= 0);

            await Program.RebuildRightSideAsync(pane, 1);
            await pane.Columns[2].LoadingTask!;         // pane.Columns[2] = c, entries "d/" and "link_a/"
            pane.Columns[2].Selected = pane.Columns[2].Entries.IndexOf("link_a/");
            Assert.True(pane.Columns[2].Selected >= 0);

            int countBefore = pane.Columns.Count;       // 3: a, b, c
            Assert.Equal(3, countBefore);

            await Program.RebuildRightSideAsync(pane, 2);

            // link_a canonically resolves to `a`, which is pane.Columns[0] -- already in the
            // chain, two hops back rather than the immediate parent. The guard seeds from the
            // full existing chain, so it must catch this exactly like the immediate-self case.
            Assert.Equal(countBefore, pane.Columns.Count);
        }
        finally { Directory.Delete(a, true); }
    }

    [Fact]
    public async Task StartPreviewLoad_DoesNotReadFileOnCallerThread()
    {
        string dir = MakeTempDir();
        string filePath = Path.Combine(dir, "a.txt");
        File.WriteAllText(filePath, "hello world");
        try
        {
            Pane pane = await MakePaneAtAsync(dir);
            pane.Columns[0].Selected = pane.Columns[0].Entries.IndexOf("a.txt");
            Assert.True(pane.Columns[0].Selected >= 0);
            pane.Preview.IsVisible = true;

            Program.StartPreviewLoad(pane);

            // Type detection (the file open + magic-byte read) must not have run synchronously
            // on this thread — it's only ever set inside the background Task.Run.
            Assert.Null(pane.Preview.CurrentType);
            Assert.True(pane.Preview.IsLoading);

            for (int i = 0; i < 200 && pane.Preview.IsLoading; i++)
                await Task.Delay(10);

            Assert.False(pane.Preview.IsLoading);
            Assert.NotNull(pane.Preview.CurrentType);
            Assert.NotNull(pane.Preview.Content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ComputePaneBodyFrameHeight_NonSplit_ReturnsFullConsoleHeight()
    {
        bool savedSplit = Program.State.SplitActive;
        try
        {
            Program.State.SplitActive = false;
            var pane = new Pane();
            Assert.Equal(Program.SafeWindowHeight(), Program.ComputePaneBodyFrameHeight(pane));
        }
        finally { Program.State.SplitActive = savedSplit; }
    }

    [Fact]
    public void ComputePaneBodyFrameHeight_Split_ReturnsHalfHeightPerPane_NotFullConsole()
    {
        bool savedSplit = Program.State.SplitActive;
        var savedPanes = Program.State.Panes;
        try
        {
            Program.State.SplitActive = true;
            var top = new Pane();
            var bottom = new Pane();
            Program.State.Panes = new List<Pane> { top, bottom };

            int fullHeight = Program.SafeWindowHeight();
            (int expectedTop, int expectedBottom) = Program.ComputeSplitPaneHeights(fullHeight);

            int actualTop = Program.ComputePaneBodyFrameHeight(top);
            int actualBottom = Program.ComputePaneBodyFrameHeight(bottom);

            Assert.Equal(expectedTop + 1, actualTop);
            Assert.Equal(expectedBottom + 1, actualBottom);

            // The core bug this fix closes: neither pane should get the FULL console height
            // while split is active — that's what caused image/video previews to be
            // rasterized ~2x taller than the space they're actually drawn into.
            Assert.True(actualTop < fullHeight, "upper pane got full console height while split — the bug this fix closes");
            Assert.True(actualBottom < fullHeight, "lower pane got full console height while split — the bug this fix closes");
        }
        finally
        {
            Program.State.SplitActive = savedSplit;
            Program.State.Panes = savedPanes;
        }
    }
}
