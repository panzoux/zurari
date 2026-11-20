using Xunit;

// o / Shift+O / Shift+Y — ペイン間のパス同期・入れ替え・幅同期。
// Program.State を書き換えるので GlobalStateTests コレクションに入れて直列化する。
[Collection("GlobalStateTests")]
public class PaneSyncSwapTests
{
    static Pane MakePane(string path, params int[] extraColumnWidths)
    {
        var pane = new Pane();
        pane.Columns.Add(new Column(path));
        foreach (int w in extraColumnWidths)
            pane.Columns.Add(new Column(path + "\\sub") { Width = w });
        pane.ActiveColumn = 0;
        return pane;
    }

    static void SetupSplit(Pane top, Pane bottom, int focused = 0)
    {
        Program.State.Panes.Clear();
        Program.State.Panes.Add(top);
        Program.State.Panes.Add(bottom);
        Program.State.SplitActive = true;
        Program.State.FocusedPane = focused;
    }

    static void SetupSingle(Pane only)
    {
        Program.State.Panes.Clear();
        Program.State.Panes.Add(only);
        Program.State.SplitActive = false;
        Program.State.FocusedPane = 0;
    }

    // --- Shift+O: 入れ替え ---

    [Fact]
    public void SwapPanes_ExchangesTopAndBottom_ButLeavesFocusOnTheSameSide()
    {
        Pane top = MakePane("C:\\top");
        Pane bottom = MakePane("C:\\bottom");
        SetupSplit(top, bottom, focused: 0);

        Program.SwapPanes();

        Assert.Same(bottom, Program.State.Panes[0]);
        Assert.Same(top, Program.State.Panes[1]);
        Assert.Equal(0, Program.State.FocusedPane);   // フォーカスは上のまま
    }

    [Fact]
    public void SwapPanes_Twice_RestoresTheOriginalOrder()
    {
        Pane top = MakePane("C:\\top");
        Pane bottom = MakePane("C:\\bottom");
        SetupSplit(top, bottom, focused: 1);

        Program.SwapPanes();
        Program.SwapPanes();

        Assert.Same(top, Program.State.Panes[0]);
        Assert.Same(bottom, Program.State.Panes[1]);
        Assert.Equal(1, Program.State.FocusedPane);
    }

    [Fact]
    public void SwapPanes_CarriesColumnWidthsWithThePane()
    {
        Pane top = MakePane("C:\\top");
        top.Columns[0].Width = 55;
        Pane bottom = MakePane("C:\\bottom");
        SetupSplit(top, bottom);

        Program.SwapPanes();

        Assert.Equal(55, Program.State.Panes[1].Columns[0].Width);
        Assert.Equal(Program.DefaultColumnWidth, Program.State.Panes[0].Columns[0].Width);
    }

    [Fact]
    public void SwapPanes_NotSplit_IsANoOp()
    {
        Pane only = MakePane("C:\\only");
        SetupSingle(only);

        Program.SwapPanes();

        Assert.Single(Program.State.Panes);
        Assert.Same(only, Program.State.Panes[0]);
    }

    // --- Shift+Y: 幅同期 ---

    [Fact]
    public void SyncColumnWidths_CopiesFocusedPaneWidthsToTheOtherPane()
    {
        Pane top = MakePane("C:\\top", 32, 32);
        Pane bottom = MakePane("C:\\bottom", 32, 32);
        top.Columns[0].Width = 20;
        top.Columns[1].Width = 44;
        top.Columns[2].Width = 12;
        SetupSplit(top, bottom, focused: 0);

        Program.SyncColumnWidthsToOtherPane();

        Assert.Equal(20, bottom.Columns[0].Width);
        Assert.Equal(44, bottom.Columns[1].Width);
        Assert.Equal(12, bottom.Columns[2].Width);
    }

    [Fact]
    public void SyncColumnWidths_AlsoWritesTheOtherPanesWidthMemory()
    {
        Pane top = MakePane("C:\\top");
        Pane bottom = MakePane("C:\\bottom");
        top.Columns[0].Width = 46;
        SetupSplit(top, bottom, focused: 0);

        Program.SyncColumnWidthsToOtherPane();

        // 相手ペインが次にこのパスを組み直しても幅が残る。
        var rebuilt = new Column("C:\\bottom");
        Program.ApplyColumnWidth(bottom, rebuilt);
        Assert.Equal(46, rebuilt.Width);
    }

    [Fact]
    public void SyncColumnWidths_DifferentColumnCounts_StopsAtTheShorterPane()
    {
        Pane top = MakePane("C:\\top", 32, 32);   // 3 columns
        Pane bottom = MakePane("C:\\bottom");     // 1 column
        top.Columns[0].Width = 18;
        SetupSplit(top, bottom, focused: 0);

        Program.SyncColumnWidthsToOtherPane();

        Assert.Single(bottom.Columns);
        Assert.Equal(18, bottom.Columns[0].Width);
    }

    [Fact]
    public void SyncColumnWidths_FocusOnBottom_CopiesUpward()
    {
        Pane top = MakePane("C:\\top");
        Pane bottom = MakePane("C:\\bottom");
        bottom.Columns[0].Width = 24;
        SetupSplit(top, bottom, focused: 1);

        Program.SyncColumnWidthsToOtherPane();

        Assert.Equal(24, top.Columns[0].Width);
    }

    [Fact]
    public void SyncColumnWidths_NotSplit_IsANoOp()
    {
        Pane only = MakePane("C:\\only");
        only.Columns[0].Width = 40;
        SetupSingle(only);

        Program.SyncColumnWidthsToOtherPane();

        Assert.Equal(40, only.Columns[0].Width);
    }

    // --- o: パス同期 ---

    [Fact]
    public async Task SyncPaneToOtherPath_MovesFocusedPaneToTheOtherPanesDirectory()
    {
        string dirA = AsyncColumnLoadTests.MakeTempDir("a.txt");
        string dirB = AsyncColumnLoadTests.MakeTempDir("b.txt");
        try
        {
            Column colA = await Program.CreateColumnAsync(dirA);
            await Program.CompleteColumnLoadAsync(colA);
            Column colB = await Program.CreateColumnAsync(dirB);
            await Program.CompleteColumnLoadAsync(colB);

            var top = new Pane();
            top.Columns.Add(colA);
            var bottom = new Pane();
            bottom.Columns.Add(colB);
            SetupSplit(top, bottom, focused: 0);

            await Program.SyncPaneToOtherPathAsync(top);

            Assert.Equal(dirB, top.Columns[top.ActiveColumn].Path);
            Assert.Equal(dirB, bottom.Columns[bottom.ActiveColumn].Path);
        }
        finally
        {
            Directory.Delete(dirA, true);
            Directory.Delete(dirB, true);
        }
    }

    [Fact]
    public async Task SyncPaneToOtherPath_BuildsTheWholeColumnChainFromTheDriveList()
    {
        // 回帰: Windows ではルートカラムがドライブ一覧なので、その下にドライブ直下の
        // カラムを積まないと最初のセグメントをドライブ一覧の中から探してしまい、
        // ジャンプが必ず "Path not found" で失敗していた。
        string dirA = AsyncColumnLoadTests.MakeTempDir("a.txt");
        string dirB = AsyncColumnLoadTests.MakeTempDir("b.txt");
        try
        {
            Column colA = await Program.CreateColumnAsync(dirA);
            await Program.CompleteColumnLoadAsync(colA);
            Column colB = await Program.CreateColumnAsync(dirB);
            await Program.CompleteColumnLoadAsync(colB);

            var top = new Pane();
            top.Columns.Add(colA);
            var bottom = new Pane();
            bottom.Columns.Add(colB);
            SetupSplit(top, bottom, focused: 0);

            await Program.SyncPaneToOtherPathAsync(top);

            // ドライブ一覧 → ドライブ直下 → …各セグメント… → 目的地
            int expectedDepth = 1 + dirB.Split(Path.DirectorySeparatorChar).Length;
            Assert.Equal(expectedDepth, top.Columns.Count);
            Assert.Equal("::DRIVES::", top.Columns[0].Path);
            Assert.Equal(Path.GetPathRoot(dirB), top.Columns[1].Path);
            Assert.Equal(dirB, top.Columns[top.ActiveColumn].Path);
            Assert.Equal(top.Columns.Count - 1, top.ActiveColumn);
        }
        finally
        {
            Directory.Delete(dirA, true);
            Directory.Delete(dirB, true);
        }
    }

    [Fact]
    public async Task SyncPaneToOtherPath_AlreadyOnTheSamePath_LeavesColumnsUntouched()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        try
        {
            Column colA = await Program.CreateColumnAsync(dir);
            await Program.CompleteColumnLoadAsync(colA);
            Column colB = await Program.CreateColumnAsync(dir);
            await Program.CompleteColumnLoadAsync(colB);

            var top = new Pane();
            top.Columns.Add(colA);
            var bottom = new Pane();
            bottom.Columns.Add(colB);
            SetupSplit(top, bottom, focused: 0);

            await Program.SyncPaneToOtherPathAsync(top);

            // 組み直さないので Column インスタンスがそのまま残る(ちらつきなし)。
            Assert.Same(colA, top.Columns[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SyncPaneToOtherPath_NotSplit_IsANoOp()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            await Program.CompleteColumnLoadAsync(col);

            var only = new Pane();
            only.Columns.Add(col);
            SetupSingle(only);

            await Program.SyncPaneToOtherPathAsync(only);

            Assert.Same(col, only.Columns[0]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
