// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class WorkerTests
{
    [Fact]
    public void ThreadJobPoolCycleDoesNotAllocateAfterWarmup()
    {
        using var root = new ExecutionRoot();
        using var worker = new ReusableJobWorker<ReusableThreadJob>(root, options: ExecutionCoreOptions.DelayedStart);
        worker.Dispose();
        for (var n = 0; n < 1000; n++)
        {
            var job = worker.Rent();
            worker.Add(job); // A stopped worker completes the abort synchronously.
            worker.Return(job);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 0; n < 1000; n++)
        {
            var job = worker.Rent();
            worker.Add(job);
            worker.Return(job);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task JobsCanBeReusedAndInvalidTransitionsAreRejected()
    {
        using var root = new ExecutionRoot();
        var count = 0;
        using var worker = new ReusableJobWorker<ReusableTaskJob>(root, (_, _) => Interlocked.Increment(ref count));
        Assert.Equal(ExecutionCoreOptions.Default, worker.Options);
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.MaxConcurrentTasks = 0);
        var job = worker.Rent();
        var firstTask = job.Task;
        Assert.Equal(ReusableJobState.Initial, job.State);
        worker.Return(job);
        Assert.Equal(ReusableJobState.Initial, job.State);
        worker.Add(job);
        await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, job.State);
        Assert.Throws<InvalidOperationException>(() => worker.Add(job));
        worker.Return(job);
        worker.Return(job);
        Assert.Equal(ReusableJobState.Pooled, job.State);
        Assert.Throws<InvalidOperationException>(() => { _ = job.WaitAsync(); });
        Assert.Throws<InvalidOperationException>(() => { _ = job.WaitAsync(TimeSpan.Zero); });
        Assert.Throws<InvalidOperationException>(() => { _ = job.Task; });
        var second = worker.Rent();
        Assert.Same(job, second);
        Assert.NotSame(firstTask, second.Task);
        worker.Add(second);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await worker.WaitForCompletion(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, count);
        worker.Return(second);
    }

    [Fact]
    public async Task ShutdownAbortsQueuedJobsAndWaitsForActiveProcessing()
    {
        using var root = new ExecutionRoot();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var worker = new TestWorker(root, async _ =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
        });
        var jobs = Enumerable.Range(0, 5).Select(_ => worker.Rent()).ToArray();
        foreach (var job in jobs)
        {
            worker.Add(job);
        }

        worker.SendSignal(ExecutionSignal.Start);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.RequestTermination();
        Assert.False(await worker.WaitForTermination(0));
        release.SetResult();
        await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(jobs.Select(job => job.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(ReusableJobState.Completed, jobs[0].State);
        Assert.All(jobs.Skip(1), job => Assert.Equal(ReusableJobState.Aborted, job.State));
        Assert.True(worker.IsCompleted);
        Assert.Equal(0, worker.NumberOfPendingJobs);
    }

    [Fact]
    public async Task ShutdownWaitsForAdditionalProcessors()
    {
        using var root = new ExecutionRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        ReusableTaskJob? firstJob = null;
        using var worker = new TestWorker(root, async job =>
        {
            var position = Interlocked.Increment(ref count);
            if (position == 2)
            {
                entered.SetResult();
            }

            await (ReferenceEquals(job, firstJob) ? releaseFirst.Task : releaseSecond.Task);
        }) { MaxConcurrentTasks = 2 };
        var jobs = Enumerable.Range(0, 6).Select(_ => worker.Rent()).ToArray();
        firstJob = jobs[0];
        foreach (var job in jobs)
        {
            worker.Add(job);
        }

        worker.SendSignal(ExecutionSignal.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.RequestTermination();
        releaseFirst.SetResult();
        await jobs[0].WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(worker.IsTerminated);
        Assert.False(worker.Terminated);
        releaseSecond.SetResult();
        await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.Terminated);
        Assert.Equal(2, count);
        Assert.True(worker.IsCompleted);
    }

    [Fact]
    public async Task ProcessingAndFinishExceptionsCannotStrandJobs()
    {
        using var root = new ExecutionRoot();
        using var worker = new TestWorker(root, _ => throw new InvalidOperationException()) { ThrowOnFinished = true };
        var first = worker.Rent();
        var second = worker.Rent();
        worker.Add(first);
        worker.Add(second);
        worker.SendSignal(ExecutionSignal.Start);
        await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Aborted, first.State);
        Assert.Equal(ReusableJobState.Aborted, second.State);
        Assert.True(await worker.WaitForCompletion(5000));
        var automatic = worker.Rent(ReusableJobFlags.ReturnToPoolOnCompletion);
        worker.Add(automatic);
        Assert.True(await worker.WaitForCompletion(5000));
        Assert.Equal(ReusableJobState.Pooled, automatic.State);
        worker.Dispose();
        await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposalBeforeStartAndAddingAfterStopReleaseWaiters()
    {
        using var root = new ExecutionRoot();
        using var worker = new TestWorker(root, _ => Task.CompletedTask) { ThrowOnFinished = true };
        var job = worker.Rent();
        worker.Add(job);
        Assert.False(await worker.WaitForCompletion(0));
        Assert.False(await worker.WaitForCompletion(TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => worker.WaitForCompletion(-2));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = worker.WaitForCompletion(TimeSpan.MaxValue); });
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.False(await worker.WaitForCompletion(source.Token));
        worker.Dispose();
        await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Aborted, job.State);
        var late = worker.Rent();
        worker.Add(late);
        await late.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Aborted, late.State);
        Assert.False(await worker.WaitForCompletion());
        Assert.True(worker.IsCompleted);
    }

    [Fact]
    public void ThreadJobsReuseTheirWaitHandleWithoutKeepingTheSignal()
    {
        using var root = new ExecutionRoot();
        using var worker = new ReusableJobWorker<ReusableThreadJob>(root, options: ExecutionCoreOptions.DelayedStart);
        var job = worker.Rent();
        Assert.False(job.Wait(TimeSpan.Zero));
        worker.Add(job);
        worker.Dispose();
        Assert.True(job.Wait(TimeSpan.FromSeconds(5)));
        job.Wait();
        worker.Return(job);
        Assert.Throws<InvalidOperationException>(() => job.Wait());
        Assert.Throws<InvalidOperationException>(() => job.Wait(TimeSpan.Zero));
        var reused = worker.Rent();
        Assert.Same(job, reused);
        Assert.False(reused.Wait(TimeSpan.Zero));
        worker.Add(reused);
        Assert.True(reused.Wait(TimeSpan.FromSeconds(5)));
    }

    private sealed class TestWorker : ReusableJobWorker<ReusableTaskJob>
    {
        private readonly Func<ReusableTaskJob, Task> process;

        public TestWorker(ExecutionGroup parent, Func<ReusableTaskJob, Task> process)
            : base(parent, options: ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.KeepAliveOnCompletion)
        {
            this.process = process;
        }

        public bool ThrowOnFinished { get; init; }

        public bool Terminated { get; private set; }

        protected override Task OnJobProcessing(ReusableTaskJob job, CancellationToken cancellationToken)
            => this.process(job);

        protected override void OnJobFinished(ReusableTaskJob job)
        {
            if (this.ThrowOnFinished)
            {
                throw new InvalidOperationException();
            }
        }

        protected override void OnTerminated() => this.Terminated = true;
    }
}
