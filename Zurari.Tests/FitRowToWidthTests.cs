using Xunit;

public class FitRowToWidthTests
{
    [Fact]
    public void FitRowToWidth_ContentFitsAndHasAnsi_PreservesAnsiCodes()
    {
        // Regression test: FitRowToWidth used to only take the ANSI-preserving path when the
        // line had zero ANSI codes at all, so any line containing SGR escapes (cursor inverse
        // video, selected-row highlighting) had them silently stripped even when the plain text
        // already fit within innerWidth and no truncation was needed.
        string input = "ab\x1b[7mc\x1b[0mde"; // plain text "abcde", width 5
        string result = Program.FitRowToWidth(input, 10);

        Assert.Contains("\x1b[7m", result);
        Assert.Contains("\x1b[0m", result);
        Assert.StartsWith(input, result);
    }

    [Fact]
    public void FitRowToWidth_ContentFitsAndHasAnsi_PadsToInnerWidth()
    {
        string input = "ab\x1b[7mc\x1b[0mde"; // plain width 5
        string result = Program.FitRowToWidth(input, 10);

        Assert.Equal(input + "     ", result); // 5 padding spaces to reach width 10
    }

    [Fact]
    public void FitRowToWidth_ContentFitsNoAnsi_PadsToInnerWidth()
    {
        string result = Program.FitRowToWidth("abc", 6);
        Assert.Equal("abc   ", result);
    }

    [Fact]
    public void FitRowToWidth_ContentExceedsWidth_TruncatesToTargetWidth()
    {
        string result = Program.FitRowToWidth("abcdefghij", 5);
        Assert.Equal(5, CharacterWidth.GetStringWidth(result));
        Assert.DoesNotContain("\x1b[", result);
    }
}
