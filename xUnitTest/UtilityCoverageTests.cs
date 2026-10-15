// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using System.Collections.Concurrent;
using System.Diagnostics;
using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class UtilityCoverageTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

    [ThreadStatic]
    private static bool insideUniqueWorkRun;

    [Fact]
    public async Task DelayedExecutorRunsTheRerunAcceptedDuringAFaultedRun()
    {
        var started = NewSignal();
        var gate = NewSignal();
        var second = NewSignal();
        var third = NewSignal();
        var calls = 0;
        var executor = new DelayedTaskExecutor(
            async _ =>
            {
                switch (Interlocked.Increment(ref calls))
                {
                    case 1:
                        started.SetResult();
                        await gate.Task;
                        throw new InvalidOperationException("first run");
                    case 2:
                        second.TrySetResult();
                        break;
                    default:
                        third.TrySetResult();
                        break;
                }
            },
            TimeSpan.Zero);

        Assert.True(executor.Request());
        await started.Task.WaitAsync(BoundedWait);
        Assert.True(executor.Request()); // Rerun accepted while the first run is still active.
        Assert.False(executor.Request()); // Coalesced into the accepted rerun.
        gate.SetResult(); // The first run now faults.
        await second.Task.WaitAsync(BoundedWait);
        Assert.Equal(2, Volatile.Read(ref calls));

        // The executor keeps working after the faulted run and its rerun.
        Assert.True(executor.Request());
        await third.Task.WaitAsync(BoundedWait);
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task DelayedExecutorRecoversAfterFaultedRunsWithoutRerun()
    {
        const int Runs = 10;
        var calls = 0;
        var ran = NewSignal();
        var executor = new DelayedTaskExecutor(
            _ =>
            {
                var call = Interlocked.Increment(ref calls);
                Volatile.Read(ref ran).TrySetResult();
                return call % 2 == 1 ? Task.FromException(new InvalidOperationException("odd run")) : Task.CompletedTask;
            },
            TimeSpan.Zero);

        for (var n = 0; n < Runs; n++)
        {
            // Each request follows the previous call of the action. Usually the faulted run has already returned the executor
            // to idle; if the request lands before the fault is handled, it is accepted as a rerun. Either way the action runs again.
            var signal = NewSignal();
            Volatile.Write(ref ran, signal);
            Assert.True(executor.Request());
            await signal.Task.WaitAsync(BoundedWait);
        }

        Assert.Equal(Runs, Volatile.Read(ref calls));
    }

    [Fact]
    public void CancellationPoolDisposesLinkedAndDerivedSourcesInsteadOfPooling()
    {
        using var parent = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        Assert.False(linked.IsCancellationRequested);
        CancellationTokenPool.Return(linked);
        Assert.Throws<ObjectDisposedException>(() => linked.TryReset());

        var derived = new DerivedCancellationTokenSource();
        CancellationTokenPool.Return(derived);
        Assert.True(derived.Disposed);
        Assert.Throws<ObjectDisposedException>(() => derived.TryReset());

        // Drain more than the whole pool: neither the linked nor the derived source may come back.
        var rented = new CancellationTokenSource[CancellationTokenPool.PoolCapacity + 16];
        for (var i = 0; i < rented.Length; i++)
        {
            rented[i] = CancellationTokenPool.Rent();
        }

        parent.Cancel();
        Assert.All(rented, source =>
        {
            Assert.False(source.IsCancellationRequested);
            Assert.NotSame(linked, source);
            Assert.NotSame(derived, source);
            Assert.Equal(typeof(CancellationTokenSource), source.GetType());
        });

        foreach (var source in rented)
        {
            CancellationTokenPool.Return(source);
        }
    }

    [Fact]
    public async Task UniqueWorkNeverRunsInlineOnTheCallingThread()
    {
        // Action: the work blocks until the caller has returned from Run(), so it cannot have run inline.
        using var release = new ManualResetEventSlim();
        var actionInline = false;
        var released = false;
        var actionCalls = 0;
        var actionWork = new UniqueWork(() =>
        {
            Interlocked.Increment(ref actionCalls);
            actionInline |= insideUniqueWorkRun;
            released = release.Wait(BoundedWait);
        });
        var first = RunFlagged(actionWork);
        Assert.False(first.IsCompleted);
        Assert.Same(first, RunFlagged(actionWork)); // Overlapping call joins the work in progress.
        release.Set();
        await first.WaitAsync(BoundedWait);
        Assert.True(released);
        Assert.False(actionInline);
        Assert.Equal(1, actionCalls);

        // Func<Task> that completes synchronously: still never inline.
        var asyncInline = false;
        var asyncCalls = 0;
        var asyncWork = new UniqueWork(() =>
        {
            Interlocked.Increment(ref asyncCalls);
            asyncInline |= insideUniqueWorkRun;
            return Task.CompletedTask;
        });
        for (var n = 0; n < 20; n++)
        {
            await RunFlagged(asyncWork).WaitAsync(BoundedWait);
        }

        Assert.False(asyncInline);
        Assert.Equal(20, asyncCalls);

        // Func<Task> that is still in progress: overlapping calls return the same task.
        var gate = NewSignal();
        var entered = NewSignal();
        var pendingWork = new UniqueWork(async () =>
        {
            entered.TrySetResult();
            await gate.Task;
        });
        var pending = RunFlagged(pendingWork);
        await entered.Task.WaitAsync(BoundedWait);
        Assert.Same(pending, RunFlagged(pendingWork));
        Assert.Same(pending, pendingWork.Run());
        gate.SetResult();
        await pending.WaitAsync(BoundedWait);
        var again = pendingWork.Run();
        Assert.NotSame(pending, again);
        await again.WaitAsync(BoundedWait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UniqueWorkSharesOneFailureWithAllOverlappingCallersAndThenStartsNewWork(bool asynchronous)
    {
        var entered = NewSignal();
        var gate = NewSignal();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var work = asynchronous
            ? new UniqueWork(async () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.SetResult();
                    await gate.Task;
                    throw new InvalidOperationException("shared");
                }
            })
            : new UniqueWork(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.SetResult();
                    release.Wait(BoundedWait);
                    throw new InvalidOperationException("shared");
                }
            });

        var first = work.Run();
        await entered.Task.WaitAsync(BoundedWait);
        var joined = new Task[16];
        Parallel.For(0, joined.Length, i => joined[i] = work.Run());
        Assert.All(joined, task => Assert.Same(first, task));
        gate.SetResult();
        release.Set();
        foreach (var task in joined.Append(first))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(BoundedWait));
            Assert.Equal("shared", error.Message);
        }

        var next = work.Run();
        Assert.NotSame(first, next);
        await next.WaitAsync(BoundedWait);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void EstimateSizeExcludesOneTimeStaticConstructorCostAndWarmsUpOnce()
    {
        // First use of the type: the static constructor allocates 1 MB, which the warm-up call must absorb.
        var size = EstimateSize.Class<HeavyStaticConstructor>();
        Assert.InRange(size, 1, 999);
        Assert.Equal(1_000_000, HeavyStaticConstructor.BufferLength);

        var calls = 0;
        var estimate = EstimateSize.Constructor(() =>
        {
            calls++;
            return new byte[64];
        });
        Assert.Equal(1001, calls);
        Assert.InRange(estimate, 64, 999);
    }

    [Fact]
    public void MicroSleepUsesThePlatformTimerAndThrowsAfterDisposal()
    {
        using var sleep = new MicroSleep();
        Assert.Equal(OperatingSystem.IsWindows() ? MicroSleepMode.WaitableTimerEx : MicroSleepMode.Nanosleep, sleep.CurrentMode);

        var stopwatch = Stopwatch.StartNew();
        sleep.Sleep(microseconds: 20_000);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(15), $"Slept only {stopwatch.Elapsed.TotalMilliseconds} ms.");

        sleep.Dispose();
        Assert.Equal(MicroSleepMode.Disposed, sleep.CurrentMode);
        Assert.Throws<ObjectDisposedException>(() => sleep.Sleep(microseconds: 1));
        Assert.Throws<ObjectDisposedException>(() => sleep.Sleep(microseconds: 0));
        sleep.Dispose();
        Assert.Equal(MicroSleepMode.Disposed, sleep.CurrentMode);
        Assert.Throws<ObjectDisposedException>(() => sleep.Sleep(microseconds: 1));
    }

    [Fact]
    public async Task PulseEventNeverLosesOrDuplicatesAPulseUnderAlignedRegistrationRaces()
    {
        const int Iterations = 2000;
        AsyncPulseEvent ev = null!;
        CancellationTokenSource source = null!;
        Task<bool> wait = null!;
        var errors = new ConcurrentQueue<Exception>();

        // Three dedicated threads plus this test: phase one releases the operations together,
        // phase two waits until all of them have returned.
        using var barrier = new Barrier(4);
        var workers = new[]
        {
            StartRaceWorker(barrier, Iterations, errors, () => wait = ev.WaitAsync(TimeSpan.FromTicks(1), source.Token)),
            StartRaceWorker(barrier, Iterations, errors, () => ev.Pulse()),
            StartRaceWorker(barrier, Iterations, errors, () => source.Cancel()),
        };

        for (var n = 0; n < Iterations; n++)
        {
            ev = new AsyncPulseEvent();
            source = new CancellationTokenSource();
            Assert.True(barrier.SignalAndWait(BarrierTimeout));
            Assert.True(barrier.SignalAndWait(BarrierTimeout));
            Assert.Empty(errors);

            // Retain mode: the single pulse is consumed by the wait or still retained, and never retained twice.
            if (!await wait.WaitAsync(BoundedWait))
            {
                Assert.True(await ev.WaitAsync(0), $"Pulse lost in iteration {n}.");
            }

            Assert.False(await ev.WaitAsync(0), $"Pulse retained twice in iteration {n}.");

            // The event is reusable.
            var next = ev.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(next.IsCompleted);
            ev.Pulse();
            Assert.True(await next.WaitAsync(BoundedWait));
            source.Dispose();
        }

        await Task.WhenAll(workers).WaitAsync(BoundedWait);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ExecutionIdAssignedInAwaitedHelperDoesNotFlowBackButEarlierIdIsShared()
    {
        Task<long[]> fresh;
        using (ExecutionContext.SuppressFlow())
        {
            fresh = Task.Run(async () =>
            {
                var assignedInHelper = await GetExecutionIdInAsyncHelperAsync();
                var caller = ExecutionId.Get();
                var sharedWithHelper = await GetExecutionIdInAsyncHelperAsync();
                return new[] { assignedInHelper, caller, sharedWithHelper, ExecutionId.Get() };
            });
        }

        var ids = await fresh.WaitAsync(BoundedWait);
        Assert.All(ids, id => Assert.NotEqual(0, id));
        Assert.NotEqual(ids[0], ids[1]); // The id first assigned inside the helper did not flow back.
        Assert.Equal(ids[1], ids[2]); // The id assigned before the call is shared with the helper.
        Assert.Equal(ids[1], ids[3]);
        Assert.DoesNotContain(ExecutionId.Get(), ids); // The fresh flow did not inherit this flow's id.
    }

    [Fact]
    public async Task SingleTaskTracksTheRunningTaskAndPropagatesCancellation()
    {
        var single = new SingleTask();
        Assert.Null(single.RunningTask);

        using var release = new ManualResetEventSlim();
        var running = single.TryRun(() => release.Wait(BoundedWait))!;
        Assert.NotNull(running);
        Assert.Same(running, single.RunningTask);
        release.Set();
        await running.WaitAsync(BoundedWait);
        Assert.Null(single.RunningTask);

        using var cancellation = new CancellationTokenSource();
        var canceled = single.TryRun(() => Task.Delay(Timeout.Infinite, cancellation.Token))!;
        Assert.NotNull(canceled);
        Assert.Same(canceled, single.RunningTask);
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<TaskCanceledException>(() => canceled.WaitAsync(BoundedWait));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(canceled.IsCanceled);
        Assert.Null(single.RunningTask);

        await single.TryRun(() => { })!.WaitAsync(BoundedWait);
        Assert.Null(single.RunningTask);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task RunFlagged(UniqueWork work)
    {
        insideUniqueWorkRun = true;
        try
        {
            return work.Run();
        }
        finally
        {
            insideUniqueWorkRun = false;
        }
    }

    private static async Task<long> GetExecutionIdInAsyncHelperAsync()
    {
        var id = ExecutionId.Get();
        await Task.Yield();
        Assert.Equal(id, ExecutionId.Get());
        return id;
    }

    private static Task StartRaceWorker(Barrier barrier, int iterations, ConcurrentQueue<Exception> errors, Action operation)
        => Task.Factory.StartNew(
            () =>
            {
                for (var n = 0; n < iterations; n++)
                {
                    if (!barrier.SignalAndWait(BarrierTimeout))
                    {
                        errors.Enqueue(new TimeoutException("Start phase timed out."));
                        return;
                    }

                    try
                    {
                        operation();
                    }
                    catch (Exception exception)
                    {
                        errors.Enqueue(exception);
                    }

                    if (!barrier.SignalAndWait(BarrierTimeout))
                    {
                        errors.Enqueue(new TimeoutException("Completion phase timed out."));
                        return;
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private sealed class HeavyStaticConstructor
    {
        private static readonly byte[] Buffer;

        static HeavyStaticConstructor()
        {
            Buffer = new byte[1_000_000];
        }

        public static int BufferLength => Buffer.Length;
    }

    private sealed class DerivedCancellationTokenSource : CancellationTokenSource
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            this.Disposed = true;
            base.Dispose(disposing);
        }
    }
}
