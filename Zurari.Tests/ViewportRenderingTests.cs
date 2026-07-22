using Xunit;

public class ViewportRenderingTests
{
    static Column MakeColumn(int entryCount, int selected = 0)
    {
        var c = new Column("C:\\test");
        for (int i = 0; i < entryCount; i++)
            c.Entries.Add($"file{i:D3}.txt");
        c.Selected = selected;
        return c;
    }

    static Line[] NewLines(int height)
    {
        var lines = new Line[height];
        for (int i = 0; i < height; i++)
            lines[i] = new Line();
        return lines;
    }

    static void SetupSinglePane()
    {
        Program.State.Panes.Clear();
        Program.State.Panes.Add(new Pane());
        Program.State.FocusedPane = 0;
    }

    [Fact]
    public void DrawColumnToLines_YOffsetZero_MatchesAbsoluteBaseline()
    {
        SetupSinglePane();
        const int frameHeight = 10;
        const int columnWidth = 32;

        Column baseline = MakeColumn(5, selected: 2);
        Line[] baselineLines = NewLines(frameHeight);
        Program.DrawColumnToLines(baselineLines, Program.State.Panes[0], baseline, 0, active: false, isLeft: false,
            frameWidth: columnWidth, frameHeight: frameHeight, isFirstVisible: true, focused: true, yOffset: 0);

        Column shifted = MakeColumn(5, selected: 2);
        Line[] shiftedLines = NewLines(frameHeight);
        Program.DrawColumnToLines(shiftedLines, Program.State.Panes[0], shifted, 0, active: false, isLeft: false,
            frameWidth: columnWidth, frameHeight: frameHeight, isFirstVisible: true, focused: true, yOffset: 0);

        for (int i = 0; i < frameHeight; i++)
            Assert.Equal(baselineLines[i].Render(columnWidth), shiftedLines[i].Render(columnWidth));
    }

    [Fact]
    public void DrawColumnToLines_NonZeroYOffset_ShiftsWrittenRowsBySameAmount()
    {
        SetupSinglePane();
        const int frameHeight = 8;
        const int columnWidth = 32;
        const int yOffset = 3;
        const int totalHeight = frameHeight + yOffset;

        Column a = MakeColumn(4, selected: 1);
        Line[] zeroOffsetLines = NewLines(frameHeight);
        Program.DrawColumnToLines(zeroOffsetLines, Program.State.Panes[0], a, 0, active: true, isLeft: false,
            frameWidth: columnWidth, frameHeight: frameHeight, isFirstVisible: true, focused: true, yOffset: 0);

        Column b = MakeColumn(4, selected: 1);
        Line[] shiftedLines = NewLines(totalHeight);
        Program.DrawColumnToLines(shiftedLines, Program.State.Panes[0], b, 0, active: true, isLeft: false,
            frameWidth: columnWidth, frameHeight: frameHeight, isFirstVisible: true, focused: true, yOffset: yOffset);

        for (int i = 0; i < frameHeight; i++)
            Assert.Equal(zeroOffsetLines[i].Render(columnWidth), shiftedLines[yOffset + i].Render(columnWidth));

        for (int i = 0; i < yOffset; i++)
            Assert.Equal(new string(' ', columnWidth), shiftedLines[i].Render(columnWidth));
    }
}

public class FooterCompositionTests
{
    [Theory]
    [InlineData("C:\\Users\\test\\short.txt", 40)]
    [InlineData("C:\\very\\long\\path\\that\\exceeds\\the\\available\\width\\file.txt", 40)]
    [InlineData("", 20)]
    public void FullPathLine_AlwaysRendersExactlyToWidth(string rawPath, int width)
    {
        string truncated = CharacterWidth.SmartTruncate(rawPath, width);
        string padded = CharacterWidth.PadToWidth(truncated, width);
        Assert.Equal(width, CharacterWidth.GetStringWidth(padded));
    }
}
