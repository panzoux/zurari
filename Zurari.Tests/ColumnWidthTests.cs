using System.Text.RegularExpressions;
using Xunit;

// Shift+←/→ によるカラム幅リサイズと、その幅を支える path→width 記憶のテスト。
public class ColumnWidthStateTests
{
    static Pane MakePane(params string[] paths)
    {
        var pane = new Pane();
        foreach (string p in paths)
            pane.Columns.Add(new Column(p));
        pane.ActiveColumn = pane.Columns.Count - 1;
        return pane;
    }

    [Fact]
    public void Column_Width_DefaultsToDefaultColumnWidth()
    {
        Assert.Equal(Program.DefaultColumnWidth, new Column("C:\\test").Width);
    }

    [Fact]
    public void ResizeActiveColumn_StepsByDeltaAndLeavesNeighboursAlone()
    {
        Pane pane = MakePane("C:\\a", "C:\\a\\b", "C:\\a\\b\\c");
        pane.ActiveColumn = 1;

        Program.ResizeActiveColumn(pane, Program.ColumnWidthStep);
        Program.ResizeActiveColumn(pane, Program.ColumnWidthStep);

        Assert.Equal(Program.DefaultColumnWidth + 2 * Program.ColumnWidthStep, pane.Columns[1].Width);
        Assert.Equal(Program.DefaultColumnWidth, pane.Columns[0].Width);
        Assert.Equal(Program.DefaultColumnWidth, pane.Columns[2].Width);
    }

    [Fact]
    public void ResizeActiveColumn_NarrowingStopsAtMinColumnWidth()
    {
        Pane pane = MakePane("C:\\a");

        for (int i = 0; i < 100; i++)
            Program.ResizeActiveColumn(pane, -Program.ColumnWidthStep);

        Assert.Equal(Program.MinColumnWidth, pane.Columns[0].Width);
    }

    [Fact]
    public void ResizeActiveColumn_WideningStopsAtTerminalWidth()
    {
        Pane pane = MakePane("C:\\a");
        int terminalWidth = Program.SafeWindowWidth();

        for (int i = 0; i < 500; i++)
            Program.ResizeActiveColumn(pane, Program.ColumnWidthStep);

        Assert.Equal(terminalWidth, pane.Columns[0].Width);
    }

    [Fact]
    public void ResizeActiveColumn_EmptyPane_DoesNotThrow()
    {
        Program.ResizeActiveColumn(new Pane(), Program.ColumnWidthStep);
    }

    [Fact]
    public void SaveThenApplyColumnWidth_RestoresWidthForSamePath()
    {
        Pane pane = MakePane("C:\\a");
        pane.Columns[0].Width = 48;
        Program.SaveColumnWidth(pane, pane.Columns[0]);

        var rebuilt = new Column("C:\\a");
        Program.ApplyColumnWidth(pane, rebuilt);

        Assert.Equal(48, rebuilt.Width);
    }

    [Fact]
    public void ApplyColumnWidth_UnknownPath_LeavesDefault()
    {
        Pane pane = MakePane("C:\\a");
        var other = new Column("C:\\somewhere\\else");
        Program.ApplyColumnWidth(pane, other);
        Assert.Equal(Program.DefaultColumnWidth, other.Width);
    }

    [Fact]
    public void SaveColumnWidth_BackAtDefault_ForgetsThePath()
    {
        Pane pane = MakePane("C:\\a");
        Column col = pane.Columns[0];

        col.Width = 48;
        Program.SaveColumnWidth(pane, col);
        col.Width = Program.DefaultColumnWidth;
        Program.SaveColumnWidth(pane, col);

        var rebuilt = new Column("C:\\a");
        Program.ApplyColumnWidth(pane, rebuilt);

        Assert.Equal(Program.DefaultColumnWidth, rebuilt.Width);
        Assert.Empty(pane.WidthMemory);
        Assert.Empty(pane.WidthMemoryKeys);
    }

    [Fact]
    public void WidthMemory_IsPerPane_SamePathKeepsTwoWidths()
    {
        Pane top = MakePane("C:\\shared");
        Pane bottom = MakePane("C:\\shared");

        top.Columns[0].Width = 50;
        Program.SaveColumnWidth(top, top.Columns[0]);
        bottom.Columns[0].Width = 20;
        Program.SaveColumnWidth(bottom, bottom.Columns[0]);

        var fromTop = new Column("C:\\shared");
        var fromBottom = new Column("C:\\shared");
        Program.ApplyColumnWidth(top, fromTop);
        Program.ApplyColumnWidth(bottom, fromBottom);

        Assert.Equal(50, fromTop.Width);
        Assert.Equal(20, fromBottom.Width);
    }

    [Fact]
    public void WidthMemory_EvictsOldestPastLimit()
    {
        var pane = new Pane();
        const int limit = 1000; // Program.CursorMemoryLimit (private const, mirrored here)

        for (int i = 0; i < limit + 5; i++)
        {
            var col = new Column("C:\\dir" + i) { Width = 40 };
            Program.SaveColumnWidth(pane, col);
        }

        Assert.Equal(limit, pane.WidthMemory.Count);
        Assert.Equal(limit, pane.WidthMemoryKeys.Count);
        Assert.False(pane.WidthMemory.ContainsKey("C:\\dir0"));      // 最古は追い出されている
        Assert.True(pane.WidthMemory.ContainsKey("C:\\dir" + (limit + 4)));
    }

    [Fact]
    public async Task RebuildRightSide_RestoresWidthWhenRevisitingSameDirectory()
    {
        // カーソル移動のたびに右側カラムは破棄・再生成される。幅が Column に載っている
        // だけだと消えてしまうので、path 記憶で戻ってくることを確認する。
        string root = AsyncColumnLoadTests.MakeTempDir();
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        Directory.CreateDirectory(Path.Combine(root, "beta"));
        try
        {
            Column rootCol = await Program.CreateColumnAsync(root);
            await Program.CompleteColumnLoadAsync(rootCol);

            var pane = new Pane();
            pane.Columns.Add(rootCol);
            pane.ActiveColumn = 0;
            rootCol.Selected = rootCol.Entries.IndexOf("alpha/");
            await Program.RebuildRightSideAsync(pane, 0);

            Assert.Equal(2, pane.Columns.Count);
            pane.Columns[1].Width = 44;
            Program.SaveColumnWidth(pane, pane.Columns[1]);

            // beta へ移る → alpha のカラムは破棄される
            rootCol.Selected = rootCol.Entries.IndexOf("beta/");
            await Program.RebuildRightSideAsync(pane, 0);
            Assert.Equal(Program.DefaultColumnWidth, pane.Columns[1].Width);

            // alpha へ戻る → 幅が復活する
            rootCol.Selected = rootCol.Entries.IndexOf("alpha/");
            await Program.RebuildRightSideAsync(pane, 0);
            Assert.Equal(44, pane.Columns[1].Width);
        }
        finally { Directory.Delete(root, true); }
    }
}

// 可変幅レイアウト: どのカラムに何セル割り当てるかの規則と、その描画結果。
public class ColumnWidthLayoutTests
{
    static readonly Regex AnsiPattern = new Regex("\u001b\\[[0-9;]*m");
    static string Plain(string s) => AnsiPattern.Replace(s, "");

    static Line[] NewLines(int height)
    {
        var lines = new Line[height];
        for (int i = 0; i < height; i++)
            lines[i] = new Line();
        return lines;
    }

    static Pane MakePane(params int[] widths)
    {
        var pane = new Pane();
        for (int i = 0; i < widths.Length; i++)
        {
            var col = new Column("C:\\test\\dir" + i) { Width = widths[i] };
            for (int e = 0; e < 3; e++)
                col.Entries.Add($"entry{e}.txt");
            pane.Columns.Add(col);
        }
        return pane;
    }

    [Fact]
    public void NextColumnWidth_FitsExactly_ReturnsDesiredWidth()
    {
        Pane pane = MakePane(30);
        Assert.Equal(30, Program.NextColumnWidth(pane, 0, remaining: 30, isFirstVisible: true));
    }

    [Fact]
    public void NextColumnWidth_DoesNotFitAndNotFirst_ReturnsZero()
    {
        Pane pane = MakePane(30);
        Assert.Equal(0, Program.NextColumnWidth(pane, 0, remaining: 29, isFirstVisible: false));
    }

    [Fact]
    public void NextColumnWidth_DoesNotFitButFirstVisible_ClampsToRemaining()
    {
        Pane pane = MakePane(60);
        Assert.Equal(25, Program.NextColumnWidth(pane, 0, remaining: 25, isFirstVisible: true));
    }

    [Fact]
    public void NextColumnWidth_FirstVisibleWithLessThanMinimumRemaining_ReturnsZero()
    {
        Pane pane = MakePane(60);
        Assert.Equal(0, Program.NextColumnWidth(pane, 0, remaining: Program.MinColumnWidth - 1, isFirstVisible: true));
    }

    [Fact]
    public void ComputeDrawnColumnsWidth_SumsOnlyTheColumnsThatFit()
    {
        Pane pane = MakePane(20, 40, 32);
        Assert.Equal(92, Program.ComputeDrawnColumnsWidth(pane, 100));
        Assert.Equal(60, Program.ComputeDrawnColumnsWidth(pane, 80));   // 3本目は入らない
        Assert.Equal(20, Program.ComputeDrawnColumnsWidth(pane, 25));   // 1本目だけ
    }

    [Fact]
    public void ComputeDrawnColumnsWidth_HonoursHorizontalScroll()
    {
        Pane pane = MakePane(20, 40, 32);
        pane.HorizontalScroll = 1;
        Assert.Equal(72, Program.ComputeDrawnColumnsWidth(pane, 100));
    }

    [Fact]
    public void DrawPaneToLines_MixedWidths_EveryRowRendersExactlyToPaneWidth()
    {
        const int width = 100;
        const int height = 10;
        Pane pane = MakePane(20, 40, 32);
        Line[] lines = NewLines(height);

        Program.DrawPaneToLines(lines, pane, 0, height, focused: true, showOwnPathLine: false, width: width);

        for (int i = 0; i < height; i++)
            Assert.Equal(width, CharacterWidth.GetStringWidth(Plain(lines[i].Render(width))));
    }

    [Fact]
    public void DrawPaneToLines_MixedWidths_SeparatorsLandAtCumulativeOffsets()
    {
        const int width = 100;
        const int height = 10;
        Pane pane = MakePane(20, 40, 32);
        Line[] lines = NewLines(height);

        Program.DrawPaneToLines(lines, pane, 0, height, focused: true, showOwnPathLine: false, width: width);

        // ヘッダ行は色付けされないので素のまま offset を確認できる。
        string header = Plain(lines[0].Render(width));
        Assert.Equal('|', header[20]);       // 1本目(幅20)の直後
        Assert.Equal('|', header[60]);       // 2本目(幅40)の直後
        Assert.DoesNotContain("|", header.Substring(61));
    }

    [Fact]
    public void DrawPaneToLines_ColumnWiderThanPane_StillDrawsFirstVisibleColumn()
    {
        const int width = 40;
        const int height = 8;
        Pane pane = MakePane(60);  // 端末より広いカラム
        Line[] lines = NewLines(height);

        Program.DrawPaneToLines(lines, pane, 0, height, focused: true, showOwnPathLine: false, width: width);

        string header = Plain(lines[0].Render(width));
        Assert.Equal(width, CharacterWidth.GetStringWidth(header));
        Assert.Contains("dir0", header);   // 真っ白ではなく、切り詰めて描かれている
    }

    [Fact]
    public void UpdateHorizontalScroll_WideColumns_ScrollsSoActiveColumnFits()
    {
        // 幅50が3本 = 150 > 端末幅120。アクティブが右端なら左端は捨てるしかない。
        Pane pane = MakePane(50, 50, 50);
        pane.ActiveColumn = 2;
        pane.HorizontalScroll = 0;

        Program.UpdateHorizontalScroll(pane);

        Assert.True(pane.HorizontalScroll <= pane.ActiveColumn);
        Assert.Equal(1, pane.HorizontalScroll);   // 2本(100セル)は入るが3本(150)は入らない
    }

    [Fact]
    public void UpdateHorizontalScroll_NarrowColumns_DoesNotForceAScroll()
    {
        // 4本合わせて40セル、端末幅120なので全部入る。右端がアクティブでもスクロール不要。
        Pane pane = MakePane(10, 10, 10, 10);
        pane.ActiveColumn = 3;
        pane.HorizontalScroll = 0;

        Program.UpdateHorizontalScroll(pane);

        Assert.Equal(0, pane.HorizontalScroll);
    }

    [Fact]
    public void UpdateHorizontalScroll_PreservesAnExistingScrollThatStillFits()
    {
        // 既存のスクロール位置は下げない(Finder 同様、右端スロットが空でも維持する)。
        Pane pane = MakePane(10, 10, 10, 10);
        pane.ActiveColumn = 3;
        pane.HorizontalScroll = 2;

        Program.UpdateHorizontalScroll(pane);

        Assert.Equal(2, pane.HorizontalScroll);
    }

    [Fact]
    public void UpdateHorizontalScroll_WideningActiveColumn_PushesScrollRight()
    {
        // 右隣のカラム分を予約したうえで、アクティブカラムまでが入るかを見る。
        // 端末幅120。30+30+30 なら予算 120-30=90 に 60 が収まるのでスクロール不要。
        Pane pane = MakePane(30, 30, 30);
        pane.ActiveColumn = 1;
        pane.HorizontalScroll = 0;
        Program.UpdateHorizontalScroll(pane);
        Assert.Equal(0, pane.HorizontalScroll);

        // 右隣を70に広げると予算は 120-70=50。60 は入らないので左端を捨てる。
        pane.Columns[2].Width = 70;
        Program.UpdateHorizontalScroll(pane);
        Assert.Equal(1, pane.HorizontalScroll);
    }
}
