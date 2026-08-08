using Xunit;

[Collection("GlobalStateTests")]
public class CreateDirectoryIntegrationTests
{
    [Fact]
    public async Task ExecuteCreateDirectoryAsync_ValidName_CreatesDirAndSelectsIt()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            if (col.LoadingTask != null) await col.LoadingTask;

            var pane = new Pane();
            pane.Columns.Add(col);
            pane.ActiveColumn = 0;

            await Program.ExecuteCreateDirectoryAsync(pane, col, "NewFolder");

            Assert.True(Directory.Exists(Path.Combine(dir, "NewFolder")));
            Column refreshed = pane.Columns[0];
            Assert.Contains("NewFolder/", refreshed.Entries);
            Assert.Equal("NewFolder/", refreshed.Entries[refreshed.Selected]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExecuteCreateDirectoryAsync_CollidingName_DoesNotCreateAnother()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        Directory.CreateDirectory(Path.Combine(dir, "Existing"));
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            if (col.LoadingTask != null) await col.LoadingTask;

            var pane = new Pane();
            pane.Columns.Add(col);
            pane.ActiveColumn = 0;

            await Program.ExecuteCreateDirectoryAsync(pane, col, "Existing");

            Assert.Single(Directory.GetDirectories(dir)); // still just the original "Existing"
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExecuteCreateDirectoryAsync_EmptyName_DoesNothing()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir();
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            if (col.LoadingTask != null) await col.LoadingTask;

            var pane = new Pane();
            pane.Columns.Add(col);
            pane.ActiveColumn = 0;

            await Program.ExecuteCreateDirectoryAsync(pane, col, "   ");

            Assert.Empty(Directory.GetDirectories(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExecuteCreateDirectoryAsync_ReservedName_DoesNotCreateOnWindows()
    {
        if (!Program.IsWindows()) return;
        string dir = AsyncColumnLoadTests.MakeTempDir();
        try
        {
            Column col = await Program.CreateColumnAsync(dir);
            if (col.LoadingTask != null) await col.LoadingTask;

            var pane = new Pane();
            pane.Columns.Add(col);
            pane.ActiveColumn = 0;

            await Program.ExecuteCreateDirectoryAsync(pane, col, "CON");

            Assert.Empty(Directory.GetDirectories(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
