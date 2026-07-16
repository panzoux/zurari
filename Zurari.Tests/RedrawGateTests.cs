using Xunit;

public class RedrawGateTests
{
    [Fact]
    public void ShouldDraw_InitialState_DrawsOnce()
    {
        var gate = new RedrawGate();
        Assert.True(gate.ShouldDraw(false));
        Assert.False(gate.ShouldDraw(false));
    }

    [Fact]
    public void Request_ThenShouldDraw_ReturnsTrueOnce()
    {
        var gate = new RedrawGate();
        gate.ShouldDraw(false); // consume initial dirty state
        gate.Request();
        Assert.True(gate.ShouldDraw(false));
        Assert.False(gate.ShouldDraw(false));
    }

    [Fact]
    public void ShouldDraw_ActivityTrue_DrawsWithoutRequest()
    {
        var gate = new RedrawGate();
        gate.ShouldDraw(false); // consume initial dirty state
        Assert.True(gate.ShouldDraw(true));
        Assert.True(gate.ShouldDraw(true));
    }

    [Fact]
    public void ShouldDraw_ActivityTrue_StillConsumesRequest()
    {
        var gate = new RedrawGate();
        gate.ShouldDraw(false); // consume initial dirty state
        gate.Request();
        Assert.True(gate.ShouldDraw(true));
        Assert.False(gate.ShouldDraw(false)); // request was consumed by the activity draw
    }

    [Fact]
    public async Task Request_FromManyThreads_NeverLost()
    {
        var gate = new RedrawGate();
        gate.ShouldDraw(false); // consume initial dirty state

        var tasks = new List<Task>();
        for (int i = 0; i < 32; i++)
            tasks.Add(Task.Run(() => gate.Request()));
        await Task.WhenAll(tasks);

        Assert.True(gate.ShouldDraw(false)); // at least one request must survive
        Assert.False(gate.ShouldDraw(false));
    }
}
