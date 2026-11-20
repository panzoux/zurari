using Xunit;

[Collection("GlobalStateTests")]
public class SearchMatchTests
{
    // ── MatchesSearchQuery ──────────────────────────────────────────────

    [Fact]
    public void Match_LiteralSubstring_CaseInsensitive()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("Report_2025.pdf", "report", false, null));
    }

    [Fact]
    public void Match_LiteralNoMatch()
    {
        Assert.False(SearchHelper.MatchesSearchQuery("budget.xlsx", "report", false, null));
    }

    [Fact]
    public void Match_EmptyQuery_ReturnsFalse()
    {
        Assert.False(SearchHelper.MatchesSearchQuery("anything.txt", "", false, null));
    }

    [Fact]
    public void Match_RegexMode_ValidPattern()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("report_2025.pdf", @"^rep.*\.pdf$", true, null));
    }

    [Fact]
    public void Match_RegexMode_InvalidPattern_ReturnsFalse()
    {
        // unclosed group — must not throw
        Assert.False(SearchHelper.MatchesSearchQuery("anything.txt", @"^([", true, null));
    }

    [Fact]
    public void Match_RegexMode_CaseInsensitive()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("REPORT.pdf", "report", true, null));
    }

    [Fact]
    public void Scan_RegexMatchTimeout_StopsAndReportsTimeout()
    {
        string entry = new string('a', 200) + "!";
        var result = SearchHelper.Scan([entry], "^(a+)+$", true, null, CancellationToken.None);

        Assert.True(result.PatternTimedOut);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task ApplyCompletedSearch_ChangedQuery_IgnoresPreviousResult()
    {
        var pane = new Pane();
        var column = new Column("unused") { Entries = ["start.txt", "old.txt", "new.txt"] };
        pane.Columns.Add(column);
        pane.Search.Active = true;
        pane.Search.Query = "old";
        Program.InvalidateSearch(pane.Search);
        Program.StartSearch(pane);
        await pane.Search.Pending!.Completion;

        pane.Search.Query = "new";
        Program.InvalidateSearch(pane.Search);
        Assert.False(Program.ApplyCompletedSearch(pane));
        Assert.Equal(0, column.Selected);

        Program.StartSearch(pane);
        await pane.Search.Pending!.Completion;
        Assert.True(Program.ApplyCompletedSearch(pane));
        Assert.Equal(2, column.Selected);
        Assert.True(pane.Search.SearchDone);
    }

    // ── FindNearestMatchIndex ───────────────────────────────────────────

    [Fact]
    public void Nearest_PicksFirstAtOrAfterAnchor()
    {
        var matches = new List<int> { 2, 7, 11 };
        Assert.Equal(1, SearchHelper.FindNearestMatchIndex(matches, anchor: 5));
    }

    [Fact]
    public void Nearest_WrapsToBeginnningWhenNoMatchAfterAnchor()
    {
        var matches = new List<int> { 2, 7 };
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(matches, anchor: 8));
    }

    [Fact]
    public void Nearest_EmptyMatches_ReturnsZero()
    {
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(new List<int>(), anchor: 3));
    }

    [Fact]
    public void Nearest_ExactAnchorMatch()
    {
        var matches = new List<int> { 5, 10 };
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(matches, anchor: 5));
    }
}
