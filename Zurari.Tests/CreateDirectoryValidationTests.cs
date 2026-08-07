using Xunit;

public class CreateDirectoryValidationTests
{
    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void IsValidDirNameChar_RejectsWindowsInvalidChars_OnWindows(char c)
    {
        if (!Program.IsWindows()) return; // this rule is Windows-specific; nothing to assert elsewhere
        Assert.False(Program.IsValidDirNameChar(c));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('Z')]
    [InlineData('0')]
    [InlineData(' ')]
    [InlineData('-')]
    [InlineData('_')]
    [InlineData('漢')]
    public void IsValidDirNameChar_AcceptsOrdinaryAndCjkChars(char c)
    {
        Assert.True(Program.IsValidDirNameChar(c));
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsReservedDeviceName()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("CON", out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsReservedDeviceNameCaseInsensitive()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("com1", out _));
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsReservedNameWithExtension()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("NUL.txt", out _));
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsTrailingDot()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("folder.", out _));
    }

    [Fact]
    public void IsInvalidDirectoryName_RejectsTrailingSpace()
    {
        if (!Program.IsWindows()) return;
        Assert.True(Program.IsInvalidDirectoryName("folder ", out _));
    }

    [Fact]
    public void IsInvalidDirectoryName_AcceptsOrdinaryName()
    {
        Assert.False(Program.IsInvalidDirectoryName("New Folder", out _));
    }

    [Fact]
    public void DirectoryNameCollides_MatchesExistingDirEntryCaseInsensitive()
    {
        var entries = new List<string> { "a.txt", "Sub/", "b.txt" };
        Assert.True(Program.DirectoryNameCollides(entries, "sub"));
    }

    [Fact]
    public void DirectoryNameCollides_MatchesExistingFileEntry()
    {
        var entries = new List<string> { "a.txt", "Sub/" };
        Assert.True(Program.DirectoryNameCollides(entries, "A.TXT"));
    }

    [Fact]
    public void DirectoryNameCollides_NoMatch_ReturnsFalse()
    {
        var entries = new List<string> { "a.txt", "Sub/" };
        Assert.False(Program.DirectoryNameCollides(entries, "NewFolder"));
    }
}
