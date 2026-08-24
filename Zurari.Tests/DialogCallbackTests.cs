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
