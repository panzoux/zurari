using Xunit;

public class AsyncColumnLoadTests
{
    internal static string MakeTempDir(params string[] fileNames)
    {
        string dir = Path.Combine(Path.GetTempPath(), "zrr_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in fileNames)
            File.WriteAllText(Path.Combine(dir, f), "");
        return dir;
    }

    [Fact]
    public async Task CreateColumnAsync_Headless_CompletesWithoutConsole()
    {
        string dir = MakeTempDir("a.txt", "b.txt");
        try
        {
            Column col = await Program.CreateColumnAsync(dir, CancellationToken.None);
            Assert.NotNull(col.LoadingTask);
            await col.LoadingTask!;
            Assert.False(col.IsLoading);
            Assert.Equal(2, col.Entries.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
