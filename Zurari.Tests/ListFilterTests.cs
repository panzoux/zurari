using Xunit;

public class ListFilterTests
{
    [Fact]
    public void Recompute_EmptyQuery_ReturnsAllItems()
    {
        var all = new List<string> { "apple.txt", "banana.txt", "報告書.pdf" };
        var result = ListFilter.Recompute(all, "", regexMode: false, migemo: null);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Recompute_LiteralQuery_FiltersCaseInsensitive()
    {
        var all = new List<string> { "Report_2025.pdf", "budget.xlsx", "Reportcard.doc" };
        var result = ListFilter.Recompute(all, "report", regexMode: false, migemo: null);
        Assert.Equal(2, result.Count);
        Assert.Contains("Report_2025.pdf", result);
        Assert.Contains("Reportcard.doc", result);
    }

    [Fact]
    public void Recompute_NoMatch_ReturnsEmptyList()
    {
        var all = new List<string> { "a.txt", "b.txt" };
        var result = ListFilter.Recompute(all, "zzz", regexMode: false, migemo: null);
        Assert.Empty(result);
    }

    [Fact]
    public void Recompute_PreservesOriginalOrder()
    {
        var all = new List<string> { "zeta.txt", "alpha.txt", "zed.txt" };
        var result = ListFilter.Recompute(all, "z", regexMode: false, migemo: null);
        Assert.Equal(new List<string> { "zeta.txt", "zed.txt" }, result);
    }

    // ── ClampSelected ──────────────────────────────────────────────

    [Fact]
    public void ClampSelected_WithinRange_Unchanged()
    {
        Assert.Equal(2, ListFilter.ClampSelected(selected: 2, filteredCount: 5));
    }

    [Fact]
    public void ClampSelected_BeyondRange_ClampsToLastIndex()
    {
        Assert.Equal(4, ListFilter.ClampSelected(selected: 10, filteredCount: 5));
    }

    [Fact]
    public void ClampSelected_EmptyList_ReturnsZero()
    {
        Assert.Equal(0, ListFilter.ClampSelected(selected: 3, filteredCount: 0));
    }

    [Fact]
    public void ClampSelected_Negative_ClampsToZero()
    {
        Assert.Equal(0, ListFilter.ClampSelected(selected: -1, filteredCount: 5));
    }
}
