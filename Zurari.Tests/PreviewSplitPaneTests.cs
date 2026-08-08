using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

[Collection("GlobalStateTests")]
public class PreviewSplitPaneTests
{
    readonly ITestOutputHelper _out;
    public PreviewSplitPaneTests(ITestOutputHelper output) { _out = output; }

    static Line[] NewLines(int n)
    {
        var lines = new Line[n];
        for (int i = 0; i < n; i++) lines[i] = new Line();
        return lines;
    }

    static Pane MakeFilePaneWithPreview()
    {
        var pane = new Pane();
        var col = new Column("C:\\test\\dir");
        col.Entries.Add("file.txt");
        col.Selected = 0;
        col.IsLoading = false;
        pane.Columns.Add(col);
        pane.Preview.IsVisible = true;
        pane.Preview.Content = new PreviewContent("Text", "42 B", "2026-01-01",
            new[] { "HELLO_PREVIEW_BODY" }, false);
        pane.Preview.IsLoading = false;
        return pane;
    }

    [Theory]
    [InlineData(0)]   // upper pane rect
    [InlineData(8)]   // lower pane rect (yOffset = topHeight + separator)
    public void Preview_RendersInsidePaneRect(int yOffset)
    {
        const int width = 80;
        const int height = 12;
        Line[] lines = NewLines(yOffset + height);
        Pane pane = MakeFilePaneWithPreview();

        Program.DrawPaneToLines(lines, pane, yOffset, height,
            focused: true, showOwnPathLine: true, width: width);

        for (int i = 0; i < lines.Length; i++)
            _out.WriteLine($"[{i:D2}] |{lines[i].Render(width)}|");

        bool found = false;
        for (int i = yOffset; i < lines.Length; i++)
            if (lines[i].Render(width).Contains("HELLO_PREVIEW_BODY")) found = true;
        Assert.True(found, $"preview body not rendered at yOffset={yOffset}");
    }

    static Pane MakePlainPane(string path)
    {
        var pane = new Pane();
        pane.Columns.Add(new Column(path));
        return pane;
    }

    [Fact]
    public void BuildFrame_SplitActive_FocusedLowerPane_RendersItsPreview()
    {
        // Save all State fields BuildFrame reads/mutates so this program-global state
        // doesn't leak into other tests.
        var savedPanes = Program.State.Panes;
        int savedFocusedPane = Program.State.FocusedPane;
        bool savedSplitActive = Program.State.SplitActive;

        try
        {
            var upper = MakePlainPane("C:\\test\\upper");
            var lower = MakeFilePaneWithPreview();

            Program.State.Panes = new List<Pane> { upper, lower };
            Program.State.FocusedPane = 1; // focus on the lower pane
            Program.State.SplitActive = true;

            string[] frame = Program.BuildFrame(80, 26);

            for (int i = 0; i < frame.Length; i++)
                _out.WriteLine($"[{i:D2}] |{frame[i]}|");

            bool found = false;
            foreach (string row in frame)
                if (row.Contains("HELLO_PREVIEW_BODY")) found = true;
            Assert.True(found, "preview body not rendered anywhere in the split frame when lower pane is focused");
        }
        finally
        {
            Program.State.Panes = savedPanes;
            Program.State.FocusedPane = savedFocusedPane;
            Program.State.SplitActive = savedSplitActive;
        }
    }
}
