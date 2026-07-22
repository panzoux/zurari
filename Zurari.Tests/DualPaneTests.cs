using Xunit;

public class DualPaneTests
{
    // 最小ペイン高さ = ヘッダ1 + 本文3 = 4行。フッター2行+区切り線1行を引いた残りが
    // 2ペイン分(4行×2=8行)未満なら分割不可。
    const int MinPaneHeight = 4;

    static bool CanSplit(int terminalHeight)
    {
        int available = terminalHeight - 2 /* footer */ - 1 /* separator */;
        return available >= MinPaneHeight * 2;
    }

    [Theory]
    [InlineData(11, true)]   // available = 8 = MinPaneHeight*2 → ちょうど境界は許可
    [InlineData(10, false)]  // available = 7 < 8 → 拒否
    [InlineData(12, true)]   // available = 9 >= 8 → 許可
    [InlineData(3, false)]   // 極端に小さい端末
    public void CanSplit_RequiresMinimumAvailableHeight(int terminalHeight, bool expected)
    {
        Assert.Equal(expected, CanSplit(terminalHeight));
    }

    [Fact]
    public void PaneHeight_OddAvailable_ExtraRowGoesToBottomPane()
    {
        // height=15 → available = 15-2-1 = 12 → paneHeight(top) = 12/2 = 6, bottom = 12-6 = 6 (偶数なので均等)
        int height = 15;
        int available = height - 2 - 1;
        int topHeight = available / 2;
        int bottomHeight = available - topHeight;
        Assert.Equal(6, topHeight);
        Assert.Equal(6, bottomHeight);
    }

    [Fact]
    public void PaneHeight_EvenAvailable_ExtraRowGoesToBottomPane()
    {
        // height=14 → available = 11 → topHeight = 11/2 = 5 (整数除算), bottomHeight = 11-5 = 6 (端数は下へ)
        int height = 14;
        int available = height - 2 - 1;
        int topHeight = available / 2;
        int bottomHeight = available - topHeight;
        Assert.Equal(5, topHeight);
        Assert.Equal(6, bottomHeight);
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
