using Xunit;

// 回帰: ダイアログの結果コールバックが Action だったころ、async ラムダを渡すと
// async void になり、最初の await のあとがスレッドプールで再開してメインループと
// 並走していた。実際に pane.Columns を並行変更して
// "Collection was modified; enumeration operation may not execute" で落ちた。
[Collection("GlobalStateTests")]
public class DialogCallbackTests
{
    static readonly ConsoleKeyInfo Enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
    static readonly ConsoleKeyInfo Escape = new ConsoleKeyInfo((char)27, ConsoleKey.Escape, false, false, false);

    [Fact]
    public async Task ConfirmDialog_AsyncCallback_IsAwaitedByTheKeyHandler()
    {
        var gate = new TaskCompletionSource();
        bool finished = false;

        Program.State.Dialog = new ConfirmDialogState
        {
            Title = "t",
            Message = "m",
            OnResult = async _ =>
            {
                await gate.Task;      // メインループが待っていなければ即座に抜けてしまう
                finished = true;
            },
        };

        Task handling = Program.HandleDialogKeyAsync(new Pane(), Enter);

        // コールバックがまだ終わっていない間、キーハンドラも完了していないこと。
        // これが async void 時代との決定的な違い(当時は即完了して finished=false のまま進んだ)。
        Assert.False(handling.IsCompleted);
        Assert.False(finished);

        gate.SetResult();
        await handling;

        Assert.True(finished);
        Assert.Null(Program.State.Dialog);
    }

    [Fact]
    public async Task TextInputDialog_AsyncCallback_IsAwaitedAndReceivesTheBuffer()
    {
        var gate = new TaskCompletionSource();
        string? received = null;

        Program.State.Dialog = new TextInputDialogState
        {
            Title = "Create Directory",
            Buffer = "1fol2",
            CursorPos = 5,
            OnResult = async value =>
            {
                await gate.Task;
                received = value;
            },
        };

        Task handling = Program.HandleDialogKeyAsync(new Pane(), Enter);
        Assert.False(handling.IsCompleted);

        gate.SetResult();
        await handling;

        Assert.Equal("1fol2", received);
    }

    [Fact]
    public async Task ListSelectDialog_AsyncCallback_IsAwaited()
    {
        var gate = new TaskCompletionSource();
        bool finished = false;

        Program.State.Dialog = new ListSelectDialogState
        {
            Title = "Bookmarks",
            AllItems = new List<string> { "C:\\a" },
            FilteredItems = new List<string> { "C:\\a" },
            Selected = 0,
            OnResult = async _ => { await gate.Task; finished = true; },
        };

        Task handling = Program.HandleDialogKeyAsync(new Pane(), Enter);
        Assert.False(handling.IsCompleted);

        gate.SetResult();
        await handling;

        Assert.True(finished);
    }

    [Fact]
    public async Task EscapeAlsoAwaitsTheCallback()
    {
        var gate = new TaskCompletionSource();
        bool? seen = true;

        Program.State.Dialog = new ConfirmDialogState
        {
            Title = "t",
            Message = "m",
            OnResult = async r => { await gate.Task; seen = r; },
        };

        Task handling = Program.HandleDialogKeyAsync(new Pane(), Escape);
        Assert.False(handling.IsCompleted);

        gate.SetResult();
        await handling;

        Assert.Null(seen);   // Esc は null を渡す
    }

    [Fact]
    public async Task SynchronousCallbacksStillWork()
    {
        bool ran = false;
        Program.State.Dialog = new ConfirmDialogState
        {
            Title = "t",
            Message = "m",
            OnResult = _ => { ran = true; return Task.CompletedTask; },
        };

        await Program.HandleDialogKeyAsync(new Pane(), Enter);

        Assert.True(ran);
        Assert.Null(Program.State.Dialog);
    }
}

// o / m の転送先解決。ペインのパス行が示す場所と一致していること。
public class PaneDestinationTests
{
    static Pane MakePane(string columnPath, params string[] entries)
    {
        var pane = new Pane();
        var col = new Column(columnPath);
        col.Entries.AddRange(entries);
        col.DirectoriesCount = entries.Count(e => e.EndsWith("/"));
        pane.Columns.Add(col);
        pane.ActiveColumn = 0;
        return pane;
    }

    [Fact]
    public void CursorOnADirectory_UsesThatDirectory()
    {
        // 回帰: 以前は Columns[ActiveColumn].Path を使っていたため D:\test へ運んでいた。
        Pane pane = MakePane("D:\\test", "1fol/", "1fol2/");
        pane.Columns[0].Selected = 0;

        Assert.Equal("D:\\test\\1fol", Program.ResolvePaneDestination(pane));
    }

    [Fact]
    public void CursorOnAFile_FallsBackToTheColumnPath()
    {
        Pane pane = MakePane("D:\\test", "1fol/", "a.txt");
        pane.Columns[0].Selected = 1;

        Assert.Equal("D:\\test", Program.ResolvePaneDestination(pane));
    }

    [Fact]
    public void EmptyColumn_FallsBackToTheColumnPath()
    {
        Pane pane = MakePane("D:\\test");
        Assert.Equal("D:\\test", Program.ResolvePaneDestination(pane));
    }

    [Fact]
    public void PaneWithNoColumns_ReturnsEmpty()
    {
        Assert.Equal("", Program.ResolvePaneDestination(new Pane()));
    }
}
