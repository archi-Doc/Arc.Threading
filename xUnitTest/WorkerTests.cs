// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class WorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedJobsUseAllConfiguredProcessorsWithoutExceedingTheLimit(bool addWhileProcessing)
    {
        using var root = new ExecutionRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var exceededLimit = 0;
        using var worker = new TestWorker(root, async _ =>
        {
            var count = Interlocked.Increment(ref active);
            firstEntered.TrySetResult();
            if (count > 4)
            {
                Interlocked.Exchange(ref exceededLimit, 1);
            }

            if (count == 4)
            {
                entered.TrySetResult();
            }

            try
            {
                await release.Task;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }) { MaxConcurrentTasks = 4 };
        var jobs = Enumerable.Range(0, 12).Select(_ => worker.Rent()).ToArray();
        try
        {
            worker.Add(jobs[0]);
            if (addWhileProcessing)
            {
                worker.SendSignal(ExecutionSignal.Start);
                await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            foreach (var job in jobs.Skip(1))
            {
                worker.Add(job);
            }

            worker.SendSignal(ExecutionSignal.Start);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, Volatile.Read(ref active));
            Assert.Equal(8, worker.PendingJobCount);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(jobs.Select(job => job.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, exceededLimit);
        Assert.All(jobs, job => Assert.Equal(ReusableJobState.Completed, job.State));
    }

    [Fact]
    public async Task ConcurrentSubmissionAcceptsEachJobOnlyOnce()
    {
        using var root = new ExecutionRoot();
        using var worker = new ReusableJobWorker<ReusableTaskJob>(root, options: ExecutionCoreOptions.DelayedStart);
        var job = worker.Rent();
        var accepted = 0;
        var rejected = 0;
        Parallel.For(0, 32, _ =>
        {
            try
            {
                worker.Add(job);
                Interlocked.Increment(ref accepted);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref rejected);
            }
        });

        Assert.Equal(1, accepted);
        Assert.Equal(31, rejected);
        Assert.Equal(1, worker.PendingJobCount);
        worker.SendSignal(ExecutionSignal.Start);
        await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, job.State);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ConcurrentSubmissionsKeepProgressingWhileTheMainProcessorIsBlocked(int concurrency)
    {
        using var root = new ExecutionRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReusableTaskJob? blockedJob = null;
        var completed = 0;
        using var worker = new TestWorker(root, async job =>
        {
            if (ReferenceEquals(job, blockedJob))
            {
                entered.TrySetResult();
                await release.Task;
            }
            else
            {
                Interlocked.Increment(ref completed);
            }
        }) { MaxConcurrentTasks = concurrency };
        blockedJob = worker.Rent();
        worker.Add(blockedJob);
        worker.SendSignal(ExecutionSignal.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var producers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                for (var n = 0; n < 250; n++)
                {
                    var job = worker.Rent();
                    worker.Add(job);
                    await job.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(ReusableJobState.Completed, job.State);
                    worker.Return(job);
                }
            }));
            await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1000, Volatile.Read(ref completed));
            Assert.False(blockedJob.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InvalidArgumentsAreRejectedAfterDisposal()
    {
        using var root = new ExecutionRoot();
        using var worker = new ReusableJobWorker<ReusableTaskJob>(root, options: ExecutionCoreOptions.DelayedStart);
        worker.Dispose();
        Assert.Throws<ArgumentNullException>(() => worker.Add(null!));
        Assert.Throws<ArgumentNullException>(() => worker.Return(null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => worker.WaitForCompletionAsync(-2));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = worker.WaitForCompletionAsync(TimeSpan.FromTicks(-1)); });
    }

    [Fact]
    public async Task CancelingOrTimingOutAJobWaitDoesNotCompleteTheJob()
    {
        var taskJob = new ReusableTaskJob();
        using var cancellation = new CancellationTokenSource();
        var wait = taskJob.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        await Assert.ThrowsAsync<TimeoutException>(() => taskJob.WaitAsync(TimeSpan.Zero));
        Assert.False(taskJob.Task.IsCompleted);
        Assert.Equal(ReusableJobState.Initial, taskJob.State);

        var blockingJob = new ReusableBlockingJob();
        Assert.False(blockingJob.Wait(TimeSpan.Zero));
        Assert.ThrowsAny<OperationCanceledException>(() => blockingJob.Wait(cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => blockingJob.Wait(TimeSpan.Zero, cancellation.Token));
        Assert.Equal(ReusableJobState.Initial, blockingJob.State);
    }

    [Fact]
    public void ThreadJobPoolCycleDoesNotAllocateAfterWarmup()
    {
        using var root = new ExecutionRoot();
        using var worker = new ReusableJobWorker<ReusableBlockingJob>(root, options: ExecutionCoreOptions.DelayedStart);
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
        Assert.Equal(ExecutionCoreOptions.None, worker.Options);
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
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
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
        Assert.False(await worker.WaitForTerminationAsync(0));
        release.SetResult();
        await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(jobs.Select(job => job.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(ReusableJobState.Completed, jobs[0].State);
        Assert.All(jobs.Skip(1), job => Assert.Equal(ReusableJobState.Aborted, job.State));
        Assert.True(worker.IsIdle);
        Assert.Equal(0, worker.PendingJobCount);
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
        Assert.True(worker.IsIdle);
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
        Assert.True(await worker.WaitForCompletionAsync(5000));
        var automatic = worker.Rent(ReusableJobOptions.ReturnToPoolOnCompletion);
        worker.Add(automatic);
        Assert.True(await worker.WaitForCompletionAsync(5000));
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
        Assert.False(await worker.WaitForCompletionAsync(0));
        Assert.False(await worker.WaitForCompletionAsync(TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => worker.WaitForCompletionAsync(-2));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = worker.WaitForCompletionAsync(TimeSpan.MaxValue); });
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.False(await worker.WaitForCompletionAsync(source.Token));
        worker.Dispose();
        await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Aborted, job.State);
        var late = worker.Rent();
        worker.Add(late);
        await late.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Aborted, late.State);
        Assert.False(await worker.WaitForCompletionAsync());
        Assert.True(worker.IsIdle);
    }

    [Fact]
    public void ThreadJobsReuseTheirWaitHandleWithoutKeepingTheSignal()
    {
        using var root = new ExecutionRoot();
        using var worker = new ReusableJobWorker<ReusableBlockingJob>(root, options: ExecutionCoreOptions.DelayedStart);
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
            : base(parent, options: ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.NoDisposeOnCompletion)
        {
            this.process = process;
        }

        public bool ThrowOnFinished { get; init; }

        public bool Terminated { get; private set; }

        protected override Task ProcessJobAsync(ReusableTaskJob job, CancellationToken cancellationToken)
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
