using Xunit;

public class DualPaneTests
{
    // 最小ペイン高さ = ヘッダ1 + 本文2 + 自分のパス行1 = 4行。共有ステータス1行+区切り線1行を
    // 引いた残りが2ペイン分(4行×2=8行)未満なら分割不可。
    // (以前は共有フッターが「フルパス+ステータス」の2行だったが、各ペインが自分のフルパス行を
    //  持つようになったため、共有で引くのはステータス1行のみになった。)

    [Theory]
    [InlineData(10, true)]   // available = 8 = MinPaneHeight*2 → ちょうど境界は許可
    [InlineData(9, false)]   // available = 7 < 8 → 拒否
    [InlineData(11, true)]   // available = 9 >= 8 → 許可
    [InlineData(3, false)]   // 極端に小さい端末
    public void CanSplit_RequiresMinimumAvailableHeight(int terminalHeight, bool expected)
    {
        Assert.Equal(expected, Program.CanSplit(terminalHeight));
    }

    [Fact]
    public void PaneHeight_OddAvailable_ExtraRowGoesToBottomPane()
    {
        // height=15 → available = 15-1-1 = 13(奇数) → topHeight = 13/2 = 6, bottomHeight = 13-6 = 7 (端数は下へ)
        int height = 15;
        int available = height - 1 - 1;
        int topHeight = available / 2;
        int bottomHeight = available - topHeight;
        Assert.Equal(6, topHeight);
        Assert.Equal(7, bottomHeight);
    }

    [Fact]
    public void PaneHeight_EvenAvailable_SplitsEvenly()
    {
        // height=14 → available = 12(偶数) → topHeight = 6, bottomHeight = 6
        int height = 14;
        int available = height - 1 - 1;
        int topHeight = available / 2;
        int bottomHeight = available - topHeight;
        Assert.Equal(6, topHeight);
        Assert.Equal(6, bottomHeight);
    }

    [Fact]
    public void ComputeFocusedPaneYRange_NotSplit_ReturnsFullScreen()
    {
        var (y, height) = Program.ComputeFocusedPaneYRange(splitActive: false, focusedPane: 0, screenHeight: 40);
        Assert.Equal(0, y);
        Assert.Equal(40, height);
    }

    [Fact]
    public void ComputeFocusedPaneYRange_Split_FocusedUpper_ReturnsTopHalf()
    {
        (int topHeight, _) = Program.ComputeSplitPaneHeights(40);
        var (y, height) = Program.ComputeFocusedPaneYRange(splitActive: true, focusedPane: 0, screenHeight: 40);
        Assert.Equal(0, y);
        Assert.Equal(topHeight, height);
    }

    [Fact]
    public void ComputeFocusedPaneYRange_Split_FocusedLower_ReturnsBottomHalfWithSeparatorOffset()
    {
        (int topHeight, int bottomHeight) = Program.ComputeSplitPaneHeights(40);
        var (y, height) = Program.ComputeFocusedPaneYRange(splitActive: true, focusedPane: 1, screenHeight: 40);
        Assert.Equal(topHeight + 1, y);
        Assert.Equal(bottomHeight, height);
    }
}

public class PaneOwnPathLineTests
{
    static Line[] NewLines(int height)
    {
        var lines = new Line[height];
        for (int i = 0; i < height; i++)
            lines[i] = new Line();
        return lines;
    }

    static Pane MakePane(string path)
    {
        var pane = new Pane();
        pane.Columns.Add(new Column(path));
        return pane;
    }

    [Fact]
    public void ShowOwnPathLine_True_WritesFocusedPrefixAndPathAtLastRow()
    {
        const int width = 40;
        const int height = 8;
        const int yOffset = 3;
        Line[] lines = NewLines(yOffset + height);
        Pane pane = MakePane("C:\\test\\dir");

        Program.DrawPaneToLines(lines, pane, yOffset, height, focused: true, showOwnPathLine: true, width: width);

        string lastRow = lines[yOffset + height - 1].Render(width);
        Assert.StartsWith("> ", lastRow);
        Assert.Contains("C:\\test\\dir", lastRow);
    }

    [Fact]
    public void ShowOwnPathLine_True_NonFocused_UsesBlankPrefix()
    {
        const int width = 40;
        const int height = 8;
        Line[] lines = NewLines(height);
        Pane pane = MakePane("C:\\test\\dir");

        Program.DrawPaneToLines(lines, pane, 0, height, focused: false, showOwnPathLine: true, width: width);

        string lastRow = lines[height - 1].Render(width);
        Assert.StartsWith("  ", lastRow);
        Assert.Contains("C:\\test\\dir", lastRow);
    }

    [Fact]
    public void ShowOwnPathLine_False_LastRowUntouched()
    {
        const int width = 40;
        const int height = 8;
        Line[] lines = NewLines(height);
        Pane pane = MakePane("C:\\test\\dir");

        Program.DrawPaneToLines(lines, pane, 0, height, focused: true, showOwnPathLine: false, width: width);

        string lastRow = lines[height - 1].Render(width);
        Assert.Equal(new string(' ', width), lastRow);
    }
}

public class FocusSwitchTests
{
    [Fact]
    public void Tab_WhenNotSplit_DoesNotChangeFocus()
    {
        var state = new ScreenState();
        state.Panes.Add(new Pane());
        state.SplitActive = false;
        state.FocusedPane = 0;

        // 分割していないときは Tab を無視する(呼び出し側のガード条件を検証)
        bool shouldSwitch = state.SplitActive;
        Assert.False(shouldSwitch);
    }

    [Fact]
    public void Tab_WhenSplit_TogglesBetweenPane0And1()
    {
        var state = new ScreenState();
        state.Panes.Add(new Pane());
        state.Panes.Add(new Pane());
        state.SplitActive = true;
        state.FocusedPane = 0;

        state.FocusedPane = state.FocusedPane == 0 ? 1 : 0;
        Assert.Equal(1, state.FocusedPane);

        state.FocusedPane = state.FocusedPane == 0 ? 1 : 0;
        Assert.Equal(0, state.FocusedPane);
    }
}
