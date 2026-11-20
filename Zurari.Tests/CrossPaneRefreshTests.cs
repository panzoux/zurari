using Xunit;

[Collection("GlobalStateTests")]
public class CrossPaneRefreshTests
{
    [Fact]
    public async Task RefreshOtherPanesAsync_OtherPaneShowsSameDir_RefreshesIt()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        var savedPanes = Program.State.Panes;
        try
        {
            Column colA = await Program.CreateColumnAsync(dir);
            await Program.CompleteColumnLoadAsync(colA);
            var paneA = new Pane();
            paneA.Columns.Add(colA);

            Column colB = await Program.CreateColumnAsync(dir);
            await Program.CompleteColumnLoadAsync(colB);
            var paneB = new Pane();
            paneB.Columns.Add(colB);

            Program.State.Panes = new List<Pane> { paneA, paneB };

            // Simulate a directory appearing on disk after both panes already loaded their listing.
            Directory.CreateDirectory(Path.Combine(dir, "NewFolder"));

            await Program.RefreshOtherPanesAsync(paneA, dir);

            Column refreshedB = paneB.Columns[0];
            await Program.CompleteColumnLoadAsync(refreshedB);
            Assert.Contains("NewFolder/", refreshedB.Entries);
        }
        finally
        {
            Program.State.Panes = savedPanes;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RefreshOtherPanesAsync_SkipsActingPane()
    {
        string dir = AsyncColumnLoadTests.MakeTempDir("a.txt");
        var savedPanes = Program.State.Panes;
        try
        {
            Column colA = await Program.CreateColumnAsync(dir);
            await Program.CompleteColumnLoadAsync(colA);
            var paneA = new Pane();
            paneA.Columns.Add(colA);
            Program.State.Panes = new List<Pane> { paneA };

            Column originalColumnInstance = paneA.Columns[0];
            await Program.RefreshOtherPanesAsync(paneA, dir);

            // actingPane is skipped entirely, so its Column instance must be untouched.
            Assert.Same(originalColumnInstance, paneA.Columns[0]);
        }
        finally
        {
            Program.State.Panes = savedPanes;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RefreshOtherPanesAsync_OtherPaneShowsUnrelatedDir_NotRefreshed()
    {
        string dirA = AsyncColumnLoadTests.MakeTempDir("a.txt");
        string dirB = AsyncColumnLoadTests.MakeTempDir("b.txt");
        var savedPanes = Program.State.Panes;
        try
        {
            Column colA = await Program.CreateColumnAsync(dirA);
            await Program.CompleteColumnLoadAsync(colA);
            var paneA = new Pane();
            paneA.Columns.Add(colA);

            Column colB = await Program.CreateColumnAsync(dirB);
            await Program.CompleteColumnLoadAsync(colB);
            var paneB = new Pane();
            paneB.Columns.Add(colB);
            Column originalColumnBInstance = paneB.Columns[0];

            Program.State.Panes = new List<Pane> { paneA, paneB };

            await Program.RefreshOtherPanesAsync(paneA, dirA);

            Assert.Same(originalColumnBInstance, paneB.Columns[0]); // untouched — different directory
        }
        finally
        {
            Program.State.Panes = savedPanes;
            Directory.Delete(dirA, true);
            Directory.Delete(dirB, true);
        }
    }
}
