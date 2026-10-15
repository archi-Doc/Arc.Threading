// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using System.Collections.Concurrent;
using System.Diagnostics;
using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class WorkerCoverageTests
{
    [Fact]
    public async Task SubmissionsRacingWithTerminationNeverHangShutdownOrStrandJobs()
    {
        const int Iterations = 1000;
        const int JobCount = 32;
        using var root = new ExecutionRoot();
        using var barrier = new Barrier(3);
        var jobs = new ReusableTaskJob[JobCount];
        YieldingWorker? current = null;
        var spin = 0;
        var threshold = 0;
        var stop = false;
        Exception? failure = null;

        // Two dedicated threads start each round together through the barrier, so the race does not depend on thread-pool availability.
        var producer = StartRacer(() =>
        {
            foreach (var job in jobs)
            {
                current!.Add(job);
            }
        });
        var terminator = StartRacer(() =>
        {
            if (threshold == 0)
            {// Terminates while the jobs are still being submitted.
                Thread.SpinWait(spin);
            }
            else
            {// Terminates while the processors are working. Busy-waits (bounded to 10 ms) so the timing is not coarsened by sleeps.
                var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 100);
                while (current!.ProcessedCount < threshold && Stopwatch.GetTimestamp() < deadline)
                {
                    Thread.SpinWait(4);
                }
            }

            current!.RequestTermination();
        });

        try
        {
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                using var worker = new YieldingWorker(root, ExecutionCoreOptions.NoDisposeOnCompletion) { MaxConcurrentTasks = 4 };
                for (var n = 0; n < JobCount; n++)
                {
                    jobs[n] = worker.Rent();
                }

                current = worker;
                spin = (iteration % 64) * 16;
                threshold = (iteration % 2) == 0 ? 0 : 1 + ((iteration / 2) % 16);
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5))); // Start the round.
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5))); // Both racers have finished.
                Assert.Null(Volatile.Read(ref failure));

                await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(jobs.Select(job => job.Task)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, worker.TerminatedCount);
                Assert.All(jobs, job => Assert.True(job.State is ReusableJobState.Completed or ReusableJobState.Aborted, $"Unexpected state {job.State}."));
                Assert.True(worker.IsIdle);
                Assert.Equal(0, worker.PendingJobCount);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            barrier.SignalAndWait(TimeSpan.FromSeconds(5)); // Releases the racers, which then exit.
            producer.Join(TimeSpan.FromSeconds(5));
            terminator.Join(TimeSpan.FromSeconds(5));
        }

        Thread StartRacer(Action action)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)) || Volatile.Read(ref stop))
                        {
                            return;
                        }

                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref failure, ex, null);
                        }

                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {// The barrier may be disposed after a failed round; never crash the test host from a background thread.
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            })
            {
                IsBackground = true,
            };

            thread.Start();
            return thread;
        }
    }

    [Theory]
    [InlineData(0)] // The worker itself is terminated.
    [InlineData(1)] // Its parent group is terminated.
    [InlineData(2)] // Its parent group is disposed.
    public async Task DelayedWorkerTerminatedBeforeStartReleasesQueuedJobs(int target)
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        using var worker = new ReusableJobWorker<ReusableTaskJob>(group, options: ExecutionCoreOptions.DelayedStart);
        var job = worker.Rent();
        worker.Add(job);
        Assert.Equal(1, worker.PendingJobCount);
        Assert.False(worker.IsIdle);

        switch (target)
        {
            case 0:
                worker.RequestTermination();
                break;

            case 1:
                group.RequestTermination();
                break;

            default:
                group.Dispose();
                break;
        }

        await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Aborted, job.State);
        Assert.True(worker.IsIdle);
        Assert.Equal(0, worker.PendingJobCount);
        Assert.True(worker.IsTerminated);
        Assert.True(await root.WaitForTerminationAsync(TimeSpan.FromSeconds(5)));

        // A job added after termination is aborted synchronously.
        var late = worker.Rent();
        worker.Add(late);
        Assert.True(late.Task.IsCompleted);
        Assert.Equal(ReusableJobState.Aborted, late.State);

        // A Start signal that arrives after cancellation abandons the never-started worker and disposes it.
        worker.SendSignal(ExecutionSignal.Start);
        Assert.True(worker.IsDisposed);
        Assert.Equal(TaskStatus.Created, worker.Task.Status);
        Assert.True(worker.IsIdle);
    }

    [Fact]
    public async Task CompletionWaitTimesOutWhileAJobIsBlockedAndAnInfiniteWaitCompletesOnRelease()
    {
        using var root = new ExecutionRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = new FuncWorker(root, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var job = worker.Rent();
        worker.Add(job);
        worker.SendSignal(ExecutionSignal.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        Assert.False(await worker.WaitForCompletionAsync(TimeSpan.FromMilliseconds(100)));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        stopwatch.Restart();
        Assert.False(await worker.WaitForCompletionAsync(100));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Running, job.State);

        var wait = worker.WaitForCompletionAsync(Timeout.InfiniteTimeSpan);
        Assert.False(wait.IsCompleted);
        release.SetResult();
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ReusableJobState.Completed, job.State);
        Assert.True(worker.IsIdle);
    }

    [Fact]
    public async Task DisposalReleasesAPendingCompletionWaitAndAbortsQueuedJobsWhileTheActiveJobCompletes()
    {
        using var root = new ExecutionRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = new FuncWorker(root, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var jobs = Enumerable.Range(0, 4).Select(_ => worker.Rent()).ToArray();
        foreach (var job in jobs)
        {
            worker.Add(job);
        }

        worker.SendSignal(ExecutionSignal.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, worker.PendingJobCount);
        var wait = worker.WaitForCompletionAsync();
        Assert.False(wait.IsCompleted);

        worker.Dispose();
        Assert.All(jobs.Skip(1), job =>
        {// Aborted synchronously by Dispose.
            Assert.Equal(ReusableJobState.Aborted, job.State);
            Assert.True(job.Task.IsCompleted);
        });
        Assert.Equal(0, worker.PendingJobCount);
        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await worker.WaitForCompletionAsync());
        Assert.Equal(ReusableJobState.Running, jobs[0].State);

        release.SetResult();
        await jobs[0].WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, jobs[0].State);
        await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, worker.TerminatedCount);
        Assert.True(worker.IsIdle);
    }

    [Fact]
    public async Task CompletionWaitReturnsFalseWhenTheCallerTokenIsCanceled()
    {
        using var root = new ExecutionRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = new FuncWorker(root, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var job = worker.Rent();
        worker.Add(job);
        worker.SendSignal(ExecutionSignal.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var infiniteWait = worker.WaitForCompletionAsync(cancellation.Token);
        var timedWait = worker.WaitForCompletionAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        Assert.False(infiniteWait.IsCompleted);
        Assert.False(timedWait.IsCompleted);
        cancellation.Cancel();
        Assert.False(await infiniteWait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await timedWait.WaitAsync(TimeSpan.FromSeconds(5)));

        // Canceling a wait does not affect the worker or its job.
        Assert.True(worker.CanContinue);
        Assert.Equal(ReusableJobState.Running, job.State);
        release.SetResult();
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ReusableJobState.Completed, job.State);
    }

    [Fact]
    public async Task CompletionWaitReturnsFalseWhenTheWorkerTerminatesWithAJobActive()
    {
        using var root = new ExecutionRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = new FuncWorker(root, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var active = worker.Rent();
        var queued = worker.Rent();
        worker.Add(active);
        worker.Add(queued);
        worker.SendSignal(ExecutionSignal.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var wait = worker.WaitForCompletionAsync();
        Assert.False(wait.IsCompleted);
        worker.RequestTermination();
        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5))); // Stopping with jobs outstanding.
        Assert.Equal(1, worker.PendingJobCount);

        release.SetResult();
        await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, active.State);
        Assert.Equal(ReusableJobState.Aborted, queued.State);
        Assert.True(worker.IsIdle);
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.Zero)); // Nothing is outstanding any more.
    }

    [Fact]
    public async Task SequentialCompletionWaitsWakeUpWithoutPolling()
    {
        const int Cycles = 50;
        using var root = new ExecutionRoot();
        var processed = 0;
        using var worker = new ReusableJobWorker<ReusableTaskJob>(root, (_, _) => Interlocked.Increment(ref processed));
        var stopwatch = Stopwatch.StartNew();
        for (var n = 0; n < Cycles; n++)
        {
            var job = worker.Rent();
            worker.Add(job);
            Assert.True(await worker.WaitForCompletionAsync());
            Assert.Equal(ReusableJobState.Completed, job.State);
            worker.Return(job);
        }

        stopwatch.Stop();
        Assert.Equal(Cycles, Volatile.Read(ref processed));

        // A polling wait needed about 100 ms per cycle (5 s in total); an idle notification needs far less.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"{Cycles} cycles took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task JobsDerivedDirectlyFromReusableJobAreProcessedByADelegateAndReportedToOnJobFinished()
    {
        using var root = new ExecutionRoot();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        object? owner = null;
        var calls = 0;
        using var worker = new CounterWorker(root, (sender, job) =>
        {
            owner = sender;
            Interlocked.Increment(ref calls);
            if (job.Value < 0)
            {// The processor delegate is synchronous, so blocking is the only way to keep this job active.
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }

            job.Value += 10;
        });

        var jobs = Enumerable.Range(1, 3).Select(value =>
        {
            var job = worker.Rent();
            job.Value = value;
            return job;
        }).ToArray();
        foreach (var job in jobs)
        {
            worker.Add(job);
        }

        worker.SendSignal(ExecutionSignal.Start);
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(worker, owner);
        Assert.Equal(new[] { 11, 12, 13 }, jobs.Select(job => job.Value));
        Assert.All(jobs, job => Assert.Equal(ReusableJobState.Completed, job.State));
        Assert.Equal(jobs, worker.Finished.Select(x => x.Job));
        Assert.All(worker.Finished, x => Assert.Equal(ReusableJobState.Completed, x.State));

        // Return then Rent reuses the instance; the custom state is left to the owner.
        worker.Return(jobs[0]);
        Assert.Equal(ReusableJobState.Pooled, jobs[0].State);
        var reused = worker.Rent();
        Assert.Same(jobs[0], reused);
        Assert.Equal(ReusableJobState.Initial, reused.State);
        Assert.Equal(ReusableJobOptions.None, reused.Options);
        Assert.Equal(11, reused.Value);

        // An automatically returned job comes back with its options reset.
        var automatic = worker.Rent(ReusableJobOptions.ReturnToPoolOnCompletion);
        Assert.True(automatic.ReturnsToPoolOnCompletion);
        worker.Add(automatic);
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ReusableJobState.Pooled, automatic.State);
        var again = worker.Rent();
        Assert.Same(automatic, again);
        Assert.Equal(ReusableJobState.Initial, again.State);
        Assert.Equal(ReusableJobOptions.None, again.Options);
        Assert.Equal(4, Volatile.Read(ref calls));

        // Dispose with pending jobs: OnJobFinished observes them as Aborted, and the active job still completes.
        var blocker = worker.Rent();
        blocker.Value = -100;
        worker.Add(blocker);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var pending = new[] { worker.Rent(), worker.Rent() };
        foreach (var job in pending)
        {
            worker.Add(job);
        }

        Assert.Equal(2, worker.PendingJobCount);
        var finishedBefore = worker.Finished.Count;
        worker.Dispose();
        var abortedByDispose = worker.Finished.Skip(finishedBefore).ToArray();
        Assert.Equal(pending, abortedByDispose.Select(x => x.Job));
        Assert.All(abortedByDispose, x => Assert.Equal(ReusableJobState.Aborted, x.State));

        release.Set();
        await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, blocker.State);
        Assert.Equal(-90, blocker.Value);
        Assert.Contains(worker.Finished, x => ReferenceEquals(x.Job, blocker) && x.State == ReusableJobState.Completed);
        Assert.Equal(5, Volatile.Read(ref calls));
        Assert.True(worker.IsIdle);
    }

    [Fact]
    public async Task ProcessingTokenIsTheWorkerAndTerminationCancelsItAbortingActiveAndQueuedJobs()
    {
        using var root = new ExecutionRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        using var worker = new FuncWorker(root, async (_, cancellationToken) =>
        {
            observed = cancellationToken;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken); // Throws OperationCanceledException on termination.
        });
        var active = worker.Rent();
        var queued = worker.Rent();
        worker.Add(active);
        worker.Add(queued);
        worker.SendSignal(ExecutionSignal.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(worker, observed.AsExecutionCore());
        Assert.Same(worker, observed.AsExecution<FuncWorker>());
        Assert.False(observed.IsCancellationRequested);
        Assert.Equal(ReusableJobState.Running, active.State);
        Assert.Equal(ReusableJobState.Pending, queued.State);

        worker.RequestTermination();
        Assert.True(observed.IsCancellationRequested);
        await Task.WhenAll(active.Task, queued.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Aborted, active.State);
        Assert.Equal(ReusableJobState.Aborted, queued.State);
        Assert.True(worker.IsIdle);
        Assert.Equal(0, worker.PendingJobCount);
        Assert.Equal(1, worker.TerminatedCount);
    }

    [Fact]
    public async Task WithCopiesOfRentedJobsGetTheirOwnPrimitiveAndStartInTheInitialState()
    {
        using var root = new ExecutionRoot();
        using var worker = new ReusableJobWorker<ReusableTaskJob>(root);
        var seed = worker.Rent();
        worker.Add(seed);
        await seed.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Return(seed);

        // A pooled instance rented for automatic return has no completion source; its copy has one.
        var automatic = worker.Rent(ReusableJobOptions.ReturnToPoolOnCompletion);
        Assert.Same(seed, automatic);
        Assert.Throws<InvalidOperationException>(() => { _ = automatic.Task; });
        var automaticCopy = automatic with { };
        Assert.NotSame(automatic, automaticCopy);
        Assert.Equal(ReusableJobState.Initial, automaticCopy.State);
        Assert.Equal(ReusableJobOptions.None, automaticCopy.Options);
        Assert.False(automaticCopy.ReturnsToPoolOnCompletion);
        Assert.False(automaticCopy.Task.IsCompleted);
        worker.Add(automatic);
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        Assert.False(automaticCopy.Task.IsCompleted);
        Assert.Equal(ReusableJobState.Initial, automaticCopy.State);

        // Completing the original does not complete the copy, which is an independent job.
        var original = worker.Rent();
        worker.Add(original);
        var copy = original with { };
        Assert.NotSame(original.Task, copy.Task);
        Assert.Equal(ReusableJobState.Initial, copy.State);
        Assert.NotEqual(original, copy);
        await original.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, original.State);
        Assert.False(copy.Task.IsCompleted);
        Assert.Equal(ReusableJobState.Initial, copy.State);
        var copyOfCompleted = original with { };
        Assert.Equal(ReusableJobState.Initial, copyOfCompleted.State);
        Assert.False(copyOfCompleted.Task.IsCompleted);
        worker.Add(copy);
        await copy.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, copy.State);

        using var blockingWorker = new ReusableJobWorker<ReusableBlockingJob>(root);
        var blockingAutomatic = blockingWorker.Rent(ReusableJobOptions.ReturnToPoolOnCompletion);
        var blockingAutomaticCopy = blockingAutomatic with { };
        Assert.Equal(ReusableJobState.Initial, blockingAutomaticCopy.State);
        Assert.Equal(ReusableJobOptions.None, blockingAutomaticCopy.Options);
        Assert.False(blockingAutomaticCopy.Wait(TimeSpan.Zero));

        var blockingOriginal = blockingWorker.Rent();
        var blockingCopy = blockingOriginal with { };
        blockingWorker.Add(blockingOriginal);
        Assert.True(blockingOriginal.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(blockingCopy.Wait(TimeSpan.Zero));
        Assert.Equal(ReusableJobState.Initial, blockingCopy.State);
        blockingWorker.Add(blockingCopy);
        Assert.True(blockingCopy.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(ReusableJobState.Completed, blockingCopy.State);
    }

    [Fact]
    public async Task JobsUseReferenceEqualityAndPooledJobsCanBePrinted()
    {
        using var root = new ExecutionRoot();
        using var worker = new ReusableJobWorker<ReusableTaskJob>(root);
        var set = new HashSet<ReusableTaskJob>();
        var job = worker.Rent();
        var hashCode = job.GetHashCode();
        Assert.True(set.Add(job));
        worker.Add(job);
        await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, job.State);
        Assert.Equal(hashCode, job.GetHashCode());
        Assert.Contains(job, set);
        Assert.True(set.Remove(job)); // Found although its state changed while it was in the set.

        worker.Return(job);
        Assert.Equal(hashCode, job.GetHashCode());
        var text = job.ToString(); // Task throws for a pooled job, so it must not be printed.
        Assert.Contains("Pooled", text);
        var automatic = worker.Rent(ReusableJobOptions.ReturnToPoolOnCompletion);
        Assert.Contains("ReturnToPoolOnCompletion", automatic.ToString());

        using var blockingWorker = new ReusableJobWorker<ReusableBlockingJob>(root);
        var blockingSet = new HashSet<ReusableBlockingJob>();
        var blockingJob = blockingWorker.Rent();
        var blockingHashCode = blockingJob.GetHashCode();
        Assert.True(blockingSet.Add(blockingJob));
        blockingWorker.Add(blockingJob);
        Assert.True(blockingJob.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(blockingHashCode, blockingJob.GetHashCode());
        Assert.True(blockingSet.Remove(blockingJob));

        Assert.NotEqual(new ReusableJob(), new ReusableJob());
        Assert.NotEqual(new ReusableTaskJob(), new ReusableTaskJob());
        Assert.NotEqual(new ReusableBlockingJob(), new ReusableBlockingJob());
        var counter = new CounterJob { Value = 1 };
        var counterCopy = counter with { };
        Assert.Equal(1, counterCopy.Value); // Derived members are copied.
        Assert.NotEqual(counter, counterCopy);
        Assert.NotEqual(new CounterJob { Value = 1 }, new CounterJob { Value = 1 });
        Assert.True(counter.Equals(counter));
    }

    [Fact]
    public async Task AutomaticallyReturnedJobsCompleteWithoutACompletionSource()
    {
        const int Rentals = 100;
        using var root = new ExecutionRoot();
        var processed = 0;
        using var worker = new ReusableJobWorker<ReusableTaskJob>(root, (_, _) => Interlocked.Increment(ref processed));
        var instances = new HashSet<ReusableTaskJob>();
        for (var n = 0; n < Rentals; n++)
        {
            var job = worker.Rent(ReusableJobOptions.ReturnToPoolOnCompletion);
            Assert.True(job.ReturnsToPoolOnCompletion);
            instances.Add(job);
            worker.Add(job);
        }

        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Rentals, Volatile.Read(ref processed));
        Assert.All(instances, job => Assert.Equal(ReusableJobState.Pooled, job.State));

        // A pooled instance rented again for automatic return has no completion source and must not be awaited.
        var pooled = worker.Rent(ReusableJobOptions.ReturnToPoolOnCompletion);
        Assert.Contains(pooled, instances);
        Assert.Throws<InvalidOperationException>(() => { _ = pooled.Task; });
        Assert.Throws<InvalidOperationException>(() => { _ = pooled.WaitAsync(); });
        worker.Add(pooled);
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ReusableJobState.Pooled, pooled.State);

        // A normal rental gets an awaitable completion source again.
        var normal = worker.Rent();
        Assert.Contains(normal, instances);
        Assert.False(normal.Task.IsCompleted);
        worker.Add(normal);
        await normal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReusableJobState.Completed, normal.State);
        Assert.Equal(Rentals + 2, Volatile.Read(ref processed));
    }

    [Fact]
    public async Task AdditionalProcessorsRunUnderTheConstructorContextNotTheProducerContext()
    {
        using var root = new ExecutionRoot();
        var local = new AsyncLocal<string?>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentQueue<string?>();
        ReusableTaskJob? first = null;

        // Create the worker in a separate flow, so "ctor" does not leak into this test's own context.
        using var worker = await Task.Run(() =>
        {
            local.Value = "ctor";
            return new FuncWorker(
                root,
                async (job, _) =>
                {
                    observed.Enqueue(local.Value);
                    if (ReferenceEquals(job, first))
                    {
                        firstEntered.TrySetResult();
                        await release.Task;
                    }
                },
                ExecutionCoreOptions.NoDisposeOnCompletion) { MaxConcurrentTasks = 4 };
        });
        Assert.Null(local.Value);

        first = worker.Rent();
        worker.Add(first);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // The main processor is now blocked.

        var producerJobs = await Task.Run(() =>
        {
            local.Value = "producer";
            var jobs = Enumerable.Range(0, 6).Select(_ => worker.Rent()).ToArray();
            foreach (var job in jobs)
            {
                worker.Add(job); // Recruits additional processors while the main processor is busy.
            }

            return jobs;
        });

        // Completing while the main processor is blocked proves that additional processors handled these jobs.
        await Task.WhenAll(producerJobs.Select(job => job.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(7, observed.Count);
        Assert.DoesNotContain("producer", observed);
        Assert.All(observed, value => Assert.Equal("ctor", value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AsynchronouslyCompletingJobsAreAllProcessed(int maxConcurrentTasks)
    {
        const int JobCount = 1000;
        using var root = new ExecutionRoot();
        using var worker = new YieldingWorker(root, ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.NoDisposeOnCompletion) { MaxConcurrentTasks = maxConcurrentTasks };
        for (var round = 1; round <= 2; round++)
        {// The second round reuses pooled jobs and runs while the worker is already started.
            var jobs = Enumerable.Range(0, JobCount).Select(_ => worker.Rent()).ToArray();
            foreach (var job in jobs)
            {
                worker.Add(job);
            }

            worker.SendSignal(ExecutionSignal.Start); // Ignored once the worker has started.
            await Task.WhenAll(jobs.Select(job => job.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(jobs, job => Assert.Equal(ReusableJobState.Completed, job.State));
            Assert.True(await worker.WaitForCompletionAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(round * JobCount, worker.ProcessedCount);
            foreach (var job in jobs)
            {
                worker.Return(job);
            }
        }

        Assert.InRange(worker.MaxActiveCount, 1, maxConcurrentTasks);
    }

    private sealed record class CounterJob : ReusableJob
    {
        public int Value { get; set; }
    }

    private sealed class CounterWorker : ReusableJobWorker<CounterJob>
    {
        public CounterWorker(ExecutionGroup parent, JobProcessor processJob)
            : base(parent, processJob, options: ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.NoDisposeOnCompletion)
        {
        }

        public ConcurrentQueue<(CounterJob Job, ReusableJobState State)> Finished { get; } = new();

        protected override void OnJobFinished(CounterJob job)
            => this.Finished.Enqueue((job, job.State));
    }

    private sealed class FuncWorker : ReusableJobWorker<ReusableTaskJob>
    {
        private readonly Func<ReusableTaskJob, CancellationToken, Task> process;
        private int terminatedCount;

        public FuncWorker(ExecutionGroup parent, Func<ReusableTaskJob, CancellationToken, Task> process, ExecutionCoreOptions options = ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.NoDisposeOnCompletion)
            : base(parent, options: options)
        {
            this.process = process;
        }

        public int TerminatedCount => Volatile.Read(ref this.terminatedCount);

        protected override Task ProcessJobAsync(ReusableTaskJob job, CancellationToken cancellationToken)
            => this.process(job, cancellationToken);

        protected override void OnTerminated()
            => Interlocked.Increment(ref this.terminatedCount);
    }

    private sealed class YieldingWorker : ReusableJobWorker<ReusableTaskJob>
    {
        private int processedCount;
        private int activeCount;
        private int maxActiveCount;
        private int terminatedCount;

        public YieldingWorker(ExecutionGroup parent, ExecutionCoreOptions options)
            : base(parent, options: options)
        {
        }

        public int ProcessedCount => Volatile.Read(ref this.processedCount);

        public int MaxActiveCount => Volatile.Read(ref this.maxActiveCount);

        public int TerminatedCount => Volatile.Read(ref this.terminatedCount);

        protected override async Task ProcessJobAsync(ReusableTaskJob job, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref this.activeCount);
            var max = Volatile.Read(ref this.maxActiveCount);
            while (active > max)
            {
                var previous = Interlocked.CompareExchange(ref this.maxActiveCount, active, max);
                if (previous == max)
                {
                    break;
                }

                max = previous;
            }

            await Task.Yield();
            Interlocked.Decrement(ref this.activeCount);
            Interlocked.Increment(ref this.processedCount);
        }

        protected override void OnTerminated()
            => Interlocked.Increment(ref this.terminatedCount);
    }
}
