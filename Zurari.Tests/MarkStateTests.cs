using System.Collections.Generic;
using Xunit;

public class MarkStateTests
{
    static Column MakeColumn(int entryCount, int selected = 0)
    {
        var c = new Column("test");
        for (int i = 0; i < entryCount; i++)
            c.Entries.Add($"file{i:D3}.txt");
        c.Selected = selected;
        return c;
    }

    [Fact]
    public void ToggleMarkAndAdvance_MarksEntryAndMovesCursorDown()
    {
        var c = MakeColumn(5, selected: 1);
        MarkHelper.ToggleMarkAndAdvance(c);
        Assert.Contains("file001.txt", c.Marked);
        Assert.Equal(2, c.Selected);
    }

    [Fact]
    public void ToggleMarkAndAdvance_TogglingMarkedEntryUnmarksIt()
    {
        var c = MakeColumn(5, selected: 0);
        MarkHelper.ToggleMarkAndAdvance(c); // mark file000.txt, cursor -> 1
        c.Selected = 0;                     // move back
        MarkHelper.ToggleMarkAndAdvance(c); // unmark file000.txt, cursor -> 1
        Assert.DoesNotContain("file000.txt", c.Marked);
    }

    [Fact]
    public void ToggleMarkAndAdvance_AtLastEntry_DoesNotAdvancePastEnd()
    {
        var c = MakeColumn(3, selected: 2);
        MarkHelper.ToggleMarkAndAdvance(c);
        Assert.Contains("file002.txt", c.Marked);
        Assert.Equal(2, c.Selected);
    }

    [Fact]
    public void ToggleMarkAndAdvance_EmptyColumn_NoOp()
    {
        var c = MakeColumn(0);
        MarkHelper.ToggleMarkAndAdvance(c);
        Assert.Empty(c.Marked);
    }

    [Fact]
    public void ClearAll_RemovesAllMarks()
    {
        var c = MakeColumn(5);
        c.Marked.Add("file000.txt");
        c.Marked.Add("file002.txt");
        MarkHelper.ClearAll(c);
        Assert.Empty(c.Marked);
    }

    [Fact]
    public void ReconcileAfterReload_RemovesNamesNoLongerInEntries()
    {
        var c = MakeColumn(3); // file000.txt, file001.txt, file002.txt
        c.Marked.Add("file000.txt");
        c.Marked.Add("file001.txt");

        // simulate reload: file001.txt was deleted externally
        c.Entries = new List<string> { "file000.txt", "file002.txt" };
        MarkHelper.ReconcileAfterReload(c);

        Assert.Contains("file000.txt", c.Marked);
        Assert.DoesNotContain("file001.txt", c.Marked);
    }

    [Fact]
    public void ReconcileAfterReload_KeepsMarksWhenSortOrderChanges()
    {
        var c = MakeColumn(3);
        c.Marked.Add("file002.txt");

        // simulate reload: same names, different order
        c.Entries = new List<string> { "file002.txt", "file000.txt", "file001.txt" };
        MarkHelper.ReconcileAfterReload(c);

        Assert.Contains("file002.txt", c.Marked);
        Assert.Single(c.Marked);
    }

    [Fact]
    public void ReconcileAfterReload_EmptyMarked_NoOp()
    {
        var c = MakeColumn(3);
        MarkHelper.ReconcileAfterReload(c);
        Assert.Empty(c.Marked);
    }
}
