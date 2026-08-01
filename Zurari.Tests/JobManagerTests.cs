using Xunit;

public class JobManagerTests
{
    static Task<FileOpResult> Instant(BackgroundJob job, CancellationToken ct)
        => Task.FromResult(new FileOpResult(1, 0, 0, null));

    static async Task<FileOpResult> WaitForCancel(BackgroundJob job, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new FileOpResult(0, 0, 0, null);
    }

    static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within timeout");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Start_JobCompletesSuccessfully_StatusBecomesCompleted()
    {
        var job = JobManager.Start("コピー", new[] { "a" }, "dest", Instant);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Completed);

        var snap = JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!;
        Assert.Equal(JobStatus.Completed, snap.Status);
        Assert.NotNull(snap.Result);
        Assert.Equal(1, snap.Result!.Succeeded);
    }

    [Fact]
    public async Task Start_AssignsIncrementingShortIds()
    {
        var job1 = JobManager.Start("コピー", new[] { "a" }, "dest", Instant);
        var job2 = JobManager.Start("コピー", new[] { "b" }, "dest", Instant);
        Assert.True(job2.ShortId > job1.ShortId);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job1.ShortId)!.Status == JobStatus.Completed);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job2.ShortId)!.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task Cancel_RunningJob_TransitionsToCancelled()
    {
        var job = JobManager.Start("移動", new[] { "a" }, "dest", WaitForCancel);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Running);

        JobManager.Cancel(job.ShortId);

        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Cancelled);
    }

    [Fact]
    public async Task GetBusyPaths_ReturnsSourcesAndDestOfActiveJobsOnly()
    {
        var job = JobManager.Start("コピー", new[] { @"C:\src\a.txt", @"C:\src\b.txt" }, @"C:\dest", WaitForCancel);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Running);

        var busy = JobManager.GetBusyPaths();
        Assert.Contains(@"C:\src\a.txt", busy);
        Assert.Contains(@"C:\src\b.txt", busy);
        Assert.Contains(@"C:\dest", busy);

        JobManager.Cancel(job.ShortId);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Cancelled);
    }

    [Fact]
    public async Task CancelAllAndWaitAsync_WaitsForCancellationWithinTimeout()
    {
        var job = JobManager.Start("移動", new[] { "a" }, "dest", WaitForCancel);
        await WaitUntilAsync(() => JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!.Status == JobStatus.Running);

        await JobManager.CancelAllAndWaitAsync(TimeSpan.FromSeconds(5));

        var snap = JobManager.Snapshot().Find(j => j.ShortId == job.ShortId)!;
        Assert.Equal(JobStatus.Cancelled, snap.Status);
    }

    [Fact]
    public async Task ConcurrencyLimit_FifthJobStaysPendingUntilSlotFrees()
    {
        var gate = new TaskCompletionSource();
        Task<FileOpResult> Blocking(BackgroundJob job, CancellationToken ct) => WaitForGate(gate);
        static async Task<FileOpResult> WaitForGate(TaskCompletionSource g)
        {
            await g.Task;
            return new FileOpResult(1, 0, 0, null);
        }

        var j1 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        var j2 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        var j3 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        var j4 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        var j5 = JobManager.Start("コピー", new[] { "a" }, "dest", Blocking);
        int[] ids = { j1.ShortId, j2.ShortId, j3.ShortId, j4.ShortId, j5.ShortId };

        await WaitUntilAsync(() =>
        {
            var snap = JobManager.Snapshot();
            int running = 0;
            foreach (int id in ids)
                if (snap.Find(j => j.ShortId == id)!.Status == JobStatus.Running) running++;
            return running == 4;
        });

        Assert.Equal(JobStatus.Pending, JobManager.Snapshot().Find(j => j.ShortId == j5.ShortId)!.Status);

        gate.SetResult();

        await WaitUntilAsync(() =>
        {
            var snap = JobManager.Snapshot();
            foreach (int id in ids)
                if (snap.Find(j => j.ShortId == id)!.Status != JobStatus.Completed) return false;
            return true;
        }, timeoutMs: 5000);
    }
}
