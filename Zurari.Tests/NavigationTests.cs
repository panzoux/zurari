using Xunit;

public class NavigationTests
{
    static Column MakeColumn(int entryCount, int selected = 0, int scrollOffset = 0)
    {
        var c = new Column("test");
        for (int i = 0; i < entryCount; i++)
            c.Entries.Add($"file{i:D3}.txt");
        c.Selected = selected;
        c.ScrollOffset = scrollOffset;
        return c;
    }

    [Fact]
    public void NavigatePage_Down_AdvancesByVisibleHeight()
    {
        var c = MakeColumn(50, selected: 0);
        NavigationHelper.PageDown(c, visibleHeight: 10);
        Assert.Equal(10, c.Selected);
    }

    [Fact]
    public void NavigatePage_Down_ClampsAtLastEntry()
    {
        var c = MakeColumn(10, selected: 5);
        NavigationHelper.PageDown(c, visibleHeight: 20);
        Assert.Equal(9, c.Selected);
    }

    [Fact]
    public void NavigatePage_Up_DecrementsByVisibleHeight()
    {
        var c = MakeColumn(50, selected: 30);
        NavigationHelper.PageUp(c, visibleHeight: 10);
        Assert.Equal(20, c.Selected);
    }

    [Fact]
    public void NavigatePage_Up_ClampsAtZero()
    {
        var c = MakeColumn(50, selected: 3);
        NavigationHelper.PageUp(c, visibleHeight: 10);
        Assert.Equal(0, c.Selected);
    }

    [Fact]
    public void NavigatePage_Home_JumpsToFirst()
    {
        var c = MakeColumn(50, selected: 25);
        NavigationHelper.GoHome(c);
        Assert.Equal(0, c.Selected);
        Assert.Equal(0, c.ScrollOffset);
    }

    [Fact]
    public void NavigatePage_End_JumpsToLast()
    {
        var c = MakeColumn(50, selected: 0);
        NavigationHelper.GoEnd(c);
        Assert.Equal(49, c.Selected);
    }

    [Fact]
    public void NavigatePage_Down_EmptyColumn_DoesNothing()
    {
        var c = MakeColumn(0);
        NavigationHelper.PageDown(c, visibleHeight: 10);
        Assert.Equal(0, c.Selected); // unchanged
    }

    [Fact]
    public void FindNearestIndex_FileRemoved_LandsOnPreviousFileInBlock()
    {
        // dirs: alpha/, delta/ ; files: bravo.txt (missing), charlie.txt, echo.txt
        var entries = new List<string> { "alpha/", "delta/", "charlie.txt", "echo.txt" };
        int idx = NavigationHelper.FindNearestIndex(entries, directoriesCount: 2, missingTarget: "bravo.txt", currentSelected: 2);
        Assert.Equal(2, idx); // "charlie.txt" is the first file, nothing sorts before "bravo.txt" among files -> next
    }

    [Fact]
    public void FindNearestIndex_FileRemoved_LandsOnPreviousWhenMiddleOfBlock()
    {
        var entries = new List<string> { "alpha.txt", "charlie.txt", "echo.txt" };
        int idx = NavigationHelper.FindNearestIndex(entries, directoriesCount: 0, missingTarget: "delta.txt", currentSelected: 1);
        Assert.Equal(1, idx); // "charlie.txt" sorts just before "delta.txt"
    }

    [Fact]
    public void FindNearestIndex_TopmostFileRemoved_LandsOnNewFirstFile()
    {
        var entries = new List<string> { "bravo.txt", "charlie.txt" };
        int idx = NavigationHelper.FindNearestIndex(entries, directoriesCount: 0, missingTarget: "alpha.txt", currentSelected: 0);
        Assert.Equal(0, idx); // nothing sorts before "alpha.txt" -> lands on new first entry
    }

    [Fact]
    public void FindNearestIndex_DirRemoved_StaysWithinDirectoryBlock()
    {
        // dirs: alpha/, charlie/ ; files: aaa.txt (would sort before "bravo/" as plain text, must not be picked)
        var entries = new List<string> { "alpha/", "charlie/", "aaa.txt" };
        int idx = NavigationHelper.FindNearestIndex(entries, directoriesCount: 2, missingTarget: "bravo/", currentSelected: 0);
        Assert.Equal(0, idx); // "alpha/" is nearest previous dir; the file block must be ignored
    }

    [Fact]
    public void FindNearestIndex_WholeBlockGone_ClampsCurrentSelected()
    {
        var entries = new List<string> { "alpha/", "charlie/" }; // no files left at all
        int idx = NavigationHelper.FindNearestIndex(entries, directoriesCount: 2, missingTarget: "zzz.txt", currentSelected: 5);
        Assert.Equal(1, idx); // clamped to last valid index
    }

    [Fact]
    public void FindNearestIndex_EverythingGone_ReturnsZero()
    {
        var entries = new List<string>();
        int idx = NavigationHelper.FindNearestIndex(entries, directoriesCount: 0, missingTarget: "gone.txt", currentSelected: 3);
        Assert.Equal(0, idx);
    }
}
