// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using System.Collections.Concurrent;
using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class LockCoverageTests
{
    private const int RaceIterations = 2000;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [ThreadStatic]
    private static bool insideTrigger; // Set on the thread that runs CancellationTokenSource.Cancel() or SemaphoreLock.Exit().

    [Fact]
    public async Task InfiniteUncancelableWaitersAreGrantedOneByOneInFifoOrder()
    {
        var mutex = new SemaphoreLock();
        Assert.True(mutex.Enter());
        var waiters = new[]
        {
            mutex.EnterAsync(-1),
            mutex.EnterAsync(CancellationToken.None),
            mutex.EnterAsync(Timeout.InfiniteTimeSpan),
            mutex.EnterAsync(),
            mutex.EnterAsync(Timeout.InfiniteTimeSpan, CancellationToken.None),
        };

        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
        for (var i = 0; i < waiters.Length; i++)
        {
            mutex.Exit();
            Assert.True(await waiters[i].WaitAsync(WaitTimeout));
            Assert.True(mutex.IsLocked);
            Assert.False(mutex.TryEnter());
            for (var j = i + 1; j < waiters.Length; j++)
            {
                Assert.False(waiters[j].IsCompleted);
            }
        }

        mutex.Exit();
        Assert.False(mutex.IsLocked);
        Assert.True(mutex.TryEnter());
        mutex.Exit();
        Assert.Throws<SynchronizationLockException>(mutex.Exit);
    }

    [Fact]
    public async Task CanceledCancelableWaitersReturnFalseWithoutDisturbingOwnerOrQueue()
    {
        var mutex = new SemaphoreLock();
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        Assert.True(mutex.Enter());

        // An already canceled token completes immediately and never joins the queue, so Exit() releases the lock.
        var immediate = mutex.EnterAsync(alreadyCanceled.Token);
        var immediateFinite = mutex.EnterAsync(TimeSpan.FromSeconds(30), alreadyCanceled.Token);
        Assert.True(immediate.IsCompletedSuccessfully);
        Assert.False(await immediate);
        Assert.True(immediateFinite.IsCompletedSuccessfully);
        Assert.False(await immediateFinite);
        mutex.Exit();
        Assert.False(mutex.IsLocked);
        Assert.True(mutex.TryEnter());

        using var headSource = new CancellationTokenSource();
        using var middleSource = new CancellationTokenSource();
        var head = mutex.EnterAsync(headSource.Token);
        var second = mutex.EnterAsync();
        var middle = mutex.EnterAsync(middleSource.Token);
        var last = mutex.EnterAsync();
        headSource.Cancel();
        middleSource.Cancel();
        Assert.False(await head.WaitAsync(WaitTimeout));
        Assert.False(await middle.WaitAsync(WaitTimeout));

        // The owner still holds the lock and the uncanceled waiters keep their order.
        Assert.True(mutex.IsLocked);
        Assert.False(mutex.TryEnter());
        Assert.False(second.IsCompleted);
        Assert.False(last.IsCompleted);

        mutex.Exit();
        Assert.True(await second.WaitAsync(WaitTimeout));
        Assert.False(last.IsCompleted);
        mutex.Exit();
        Assert.True(await last.WaitAsync(WaitTimeout));
        mutex.Exit();
        Assert.False(mutex.IsLocked);
        Assert.True(mutex.TryEnter());
        mutex.Exit();
        Assert.False(mutex.IsLocked);
    }

    [Fact]
    public async Task GrantedCancelableWaiterIgnoresLaterCancellation()
    {
        var mutex = new SemaphoreLock();
        Assert.True(mutex.Enter());
        using var source = new CancellationTokenSource();
        var waiter = mutex.EnterAsync(source.Token);
        var next = mutex.EnterAsync();
        mutex.Exit();
        Assert.True(await waiter.WaitAsync(WaitTimeout));

        // The grant unregistered the callback: canceling now neither releases the lock nor removes the next waiter.
        source.Cancel();
        Assert.True(mutex.IsLocked);
        Assert.False(next.IsCompleted);
        mutex.Exit();
        Assert.True(await next.WaitAsync(WaitTimeout));
        mutex.Exit();
        Assert.False(mutex.IsLocked);
        Assert.True(mutex.TryEnter());
        mutex.Exit();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CallerContinuationNeverRunsInsideCancelOrExit(bool finiteTimeout, bool grantByExit)
    {
        var mutex = new SemaphoreLock();
        var timeout = finiteTimeout ? TimeSpan.FromSeconds(30) : Timeout.InfiniteTimeSpan;
        for (var i = 0; i < 20; i++)
        {
            Assert.True(mutex.TryEnter());
            using var source = new CancellationTokenSource();
            var observation = EnterAndObserveAsync(mutex, timeout, source.Token);
            Assert.False(observation.IsCompleted);

            insideTrigger = true;
            try
            {
                if (grantByExit)
                {
                    mutex.Exit();
                }
                else
                {
                    source.Cancel();
                }
            }
            finally
            {
                insideTrigger = false;
            }

            var (acquired, observedInsideTrigger) = await observation.WaitAsync(WaitTimeout);
            Assert.Equal(grantByExit, acquired);
            Assert.False(observedInsideTrigger);

            // Either the granted waiter or the original owner holds the lock now.
            Assert.True(mutex.IsLocked);
            mutex.Exit();
            Assert.False(mutex.IsLocked);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRacingWithExitNeverBreaksExclusion(bool finiteTimeout)
    {
        var mutex = new SemaphoreLock();
        var failures = new ConcurrentQueue<Exception>();
        CancellationTokenSource? source = null;
        using var barrier = new Barrier(3);
        var canceler = StartRaceThread(barrier, RaceIterations, failures, i =>
        {
            Thread.SpinWait(i % 64);
            source!.Cancel();
        });
        var releaser = StartRaceThread(barrier, RaceIterations, failures, i =>
        {
            Thread.SpinWait((i * 7) % 64);
            mutex.Exit();
        });

        for (var i = 0; i < RaceIterations; i++)
        {
            Assert.True(mutex.TryEnter());
            source = new CancellationTokenSource();
            var waiter = finiteTimeout ? mutex.EnterAsync(TimeSpan.FromSeconds(30), source.Token) : mutex.EnterAsync(source.Token);
            Assert.False(waiter.IsCompleted);
            Assert.True(barrier.SignalAndWait(WaitTimeout)); // Cancel() and Exit() race now.
            Assert.True(barrier.SignalAndWait(WaitTimeout)); // Both have returned.
            Assert.Empty(failures);
            if (!finiteTimeout)
            {// Both outcomes complete the task synchronously inside Cancel() or Exit().
                Assert.True(waiter.IsCompleted);
            }

            if (await waiter.WaitAsync(WaitTimeout))
            {// Exit() handed the lock to the waiter, which now owns it exclusively.
                Assert.True(mutex.IsLocked);
                Assert.False(mutex.TryEnter());
                mutex.Exit();
            }

            Assert.False(mutex.IsLocked);
            Assert.True(mutex.TryEnter());
            mutex.Exit();
            source.Dispose();
        }

        Assert.True(canceler.Join(WaitTimeout));
        Assert.True(releaser.Join(WaitTimeout));
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRacingWithRegistrationNeverStrandsTheQueue(bool finiteTimeout)
    {
        var mutex = new SemaphoreLock();
        var failures = new ConcurrentQueue<Exception>();
        CancellationTokenSource? source = null;
        using var barrier = new Barrier(2);
        var canceler = StartRaceThread(barrier, RaceIterations, failures, i =>
        {
            Thread.SpinWait(i % 128);
            source!.Cancel();
        });

        for (var i = 0; i < RaceIterations; i++)
        {
            Assert.True(mutex.TryEnter());
            source = new CancellationTokenSource();
            Assert.True(barrier.SignalAndWait(WaitTimeout)); // Cancel() starts while the waiter registers.
            var waiter = finiteTimeout ? mutex.EnterAsync(TimeSpan.FromSeconds(30), source.Token) : mutex.EnterAsync(source.Token);
            var next = mutex.EnterAsync();
            Assert.True(barrier.SignalAndWait(WaitTimeout)); // Cancel() has returned.
            Assert.Empty(failures);
            if (!finiteTimeout)
            {// Whether canceled before, during or after registration, the result is set by the time Cancel() returns.
                Assert.True(waiter.IsCompleted);
            }

            Assert.False(await waiter.WaitAsync(WaitTimeout));
            Assert.True(mutex.IsLocked);
            Assert.False(next.IsCompleted);
            mutex.Exit();
            Assert.True(await next.WaitAsync(WaitTimeout));
            mutex.Exit();
            Assert.False(mutex.IsLocked);
            Assert.True(mutex.TryEnter());
            mutex.Exit();
            source.Dispose();
        }

        Assert.True(canceler.Join(WaitTimeout));
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedInterruptionDuringAsyncQueueCleanupDoesNotStrandTheLock(bool transferBeforeCleanup)
    {
        var mutex = new SemaphoreLock();
        Assert.True(mutex.Enter());
        var firstWaiter = mutex.EnterAsync(); // A queued async waiter sends the synchronous Enter() into the async queue.
        Exception? interruption = null;
        var thread = new Thread(() =>
        {
            try
            {
                mutex.Enter();
                mutex.Exit();
            }
            catch (Exception exception)
            {
                interruption = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(SpinWait.SpinUntil(() => IsBlocked(thread), WaitTimeout));

        // Holding the monitor keeps the interrupted thread in its cleanup Monitor.Enter(), where the second interruption lands.
        lock (mutex)
        {
            thread.Interrupt();
            WaitUntilBlockedAgain(thread);
            thread.Interrupt();
            WaitUntilBlockedAgain(thread);
            mutex.Exit();
            if (transferBeforeCleanup)
            {
                // The first asynchronous waiter owns the lock, then passes it to the interrupted thread.
                mutex.Exit();
            }
        }

        Assert.True(thread.Join(WaitTimeout));
        Assert.IsType<ThreadInterruptedException>(interruption);
        Assert.True(await firstWaiter.WaitAsync(WaitTimeout));
        if (!transferBeforeCleanup)
        {
            mutex.Exit();
        }

        Assert.False(mutex.IsLocked);
        Assert.True(mutex.TryEnter());
        mutex.Exit();
        Assert.False(mutex.IsLocked);
    }

    [Fact]
    public async Task EnterScopeAsyncSharesOneCompletedTaskForUncontendedCalls()
    {
        var mutex = new SemaphoreLock();
        var first = mutex.EnterScopeAsync();
        Assert.True(first.IsCompletedSuccessfully);
        var scope = await first;
        Assert.True(scope.IsLocked);
        Assert.Same(mutex, scope.Lockable);
        Assert.True(mutex.IsLocked);
        scope.Dispose();
        Assert.False(scope.IsLocked);
        Assert.False(mutex.IsLocked);

        var second = mutex.EnterScopeAsync();
        Assert.Same(first, second);
        var secondScope = await second;
        Assert.True(secondScope.IsLocked); // Each read of the shared result is a fresh acquired copy.
        Assert.True(mutex.IsLocked);

        var otherMutex = new SemaphoreLock();
        var other = otherMutex.EnterScopeAsync();
        Assert.NotSame(first, other);
        (await other).Dispose();
        Assert.False(otherMutex.IsLocked);

        // A contended call returns its own task, which completes with a working scope after the owner leaves.
        var contended = mutex.EnterScopeAsync();
        Assert.NotSame(first, contended);
        Assert.False(contended.IsCompleted);
        secondScope.Dispose();
        secondScope.Dispose();
        var contendedScope = await contended.WaitAsync(WaitTimeout);
        Assert.True(contendedScope.IsLocked);
        Assert.Same(mutex, contendedScope.Lockable);
        Assert.True(mutex.IsLocked);
        Assert.False(mutex.TryEnter());
        contendedScope.Dispose();
        Assert.False(mutex.IsLocked);

        using (await mutex.EnterScopeAsync())
        {
            Assert.True(mutex.IsLocked);
        }

        Assert.False(mutex.IsLocked);
        Assert.Same(first, mutex.EnterScopeAsync());
        mutex.Exit();
        Assert.True(mutex.TryEnter());
        mutex.Exit();
    }

#if DEBUG
    [Fact(Skip = "Allocation assertions require optimized async state machines in a Release build.")]
#else
    [Fact]
#endif
    public void UncontendedEnterScopeAsyncCyclesDoNotAllocate()
    {
        var mutex = new SemaphoreLock();
        RunScopeCycles(mutex, 1000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        RunScopeCycles(mutex, 1000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.False(mutex.IsLocked);
    }

    [Fact]
    public void MonitorLockOwnershipIsVisibleOnlyToTheOwningThread()
    {
        var mutex = new MonitorLock();
        Assert.True(mutex.Enter());
        Assert.True(mutex.IsLocked);
        bool? lockedElsewhere = null;
        Exception? foreignExit = null;
        RunOnDedicatedThread(() =>
        {
            lockedElsewhere = mutex.IsLocked;
            foreignExit = Record.Exception(mutex.Exit);
        });
        Assert.False(lockedElsewhere);
        Assert.IsType<SynchronizationLockException>(foreignExit);
        Assert.True(mutex.IsLocked); // The rejected foreign Exit() did not release the lock.
        mutex.Exit();
        Assert.False(mutex.IsLocked);

        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool? lockedByOwner = null;
        var owner = new Thread(() =>
        {
            using (mutex.EnterScope())
            {
                lockedByOwner = mutex.IsLocked;
                acquired.Set();
                release.Wait(WaitTimeout);
            }
        })
        {
            IsBackground = true,
        };
        owner.Start();
        Assert.True(acquired.Wait(WaitTimeout));
        Assert.True(lockedByOwner);
        Assert.False(mutex.IsLocked); // Held by the other thread, not by this one.
        Assert.Throws<SynchronizationLockException>(mutex.Exit);
        release.Set();
        Assert.True(owner.Join(WaitTimeout));
        Assert.False(mutex.IsLocked);
        using (mutex.EnterScope())
        {
            Assert.True(mutex.IsLocked);
        }

        Assert.False(mutex.IsLocked);
    }

    [Fact]
    public async Task LockScopeReleasesOnlyWhatItAcquired()
    {
        var refusing = new CountingLock(accept: false);
        var scope = new LockScope(refusing);
        Assert.False(scope.IsLocked);
        Assert.Same(refusing, scope.Lockable);
        Assert.Equal(1, refusing.EnterCalls);
        scope.Dispose();
        Assert.Equal(0, refusing.ExitCalls);

        var defaultScope = ((ILockable)refusing).EnterScope();
        Assert.False(defaultScope.IsLocked);
        defaultScope.Dispose();
        Assert.Equal(2, refusing.EnterCalls);
        Assert.Equal(0, refusing.ExitCalls);

        var asyncScope = await ((IAsyncLockable)refusing).EnterScopeAsync();
        Assert.False(asyncScope.IsLocked);
        Assert.Same(refusing, asyncScope.Lockable);
        asyncScope.Dispose();
        Assert.Equal(1, refusing.EnterAsyncCalls);
        Assert.Equal(0, refusing.ExitCalls);

        var accepting = new CountingLock(accept: true);
        var acceptedScope = await ((IAsyncLockable)accepting).EnterScopeAsync();
        Assert.True(acceptedScope.IsLocked);
        Assert.True(accepting.IsLocked);
        acceptedScope.Dispose();
        acceptedScope.Dispose();
        Assert.False(acceptedScope.IsLocked);
        Assert.Equal(1, accepting.EnterAsyncCalls);
        Assert.Equal(1, accepting.ExitCalls);
        using (((ILockable)accepting).EnterScope())
        {
            Assert.True(accepting.IsLocked);
        }

        Assert.Equal(1, accepting.EnterCalls);
        Assert.Equal(2, accepting.ExitCalls);
        Assert.False(accepting.IsLocked);
    }

    [Fact]
    public async Task SynchronousEnterWaitsBehindQueuedAsyncWaitersAndPreservesExclusion()
    {
        var mutex = new SemaphoreLock();
        using var neverCanceled = new CancellationTokenSource();
        Assert.True(mutex.Enter());
        var asyncWaiters = new[]
        {
            mutex.EnterAsync(),
            mutex.EnterAsync(neverCanceled.Token),
            mutex.EnterAsync(TimeSpan.FromSeconds(30)),
        };

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.True(mutex.Enter());
                entered.Set();
                Assert.True(release.Wait(WaitTimeout));
                mutex.Exit();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(SpinWait.SpinUntil(() => IsBlocked(thread), WaitTimeout));

        foreach (var waiter in asyncWaiters)
        {
            Assert.False(entered.IsSet);
            mutex.Exit();
            Assert.True(await waiter.WaitAsync(WaitTimeout));
            Assert.True(mutex.IsLocked);
        }

        Assert.False(entered.IsSet); // The last async waiter still owns the lock.
        mutex.Exit();
        Assert.True(entered.Wait(WaitTimeout));
        Assert.True(mutex.IsLocked);
        Assert.False(mutex.TryEnter());
        release.Set();
        Assert.True(thread.Join(WaitTimeout));
        Assert.Null(failure);
        Assert.False(mutex.IsLocked);

        // Mixed contention: synchronous threads and every kind of async waiter share one non-atomic counter.
        const int Rounds = 200;
        var counter = 0;
        var inside = 0;
        var violations = 0;
        var failures = new ConcurrentQueue<Exception>();
        void EnterCritical()
        {
            if (Interlocked.Increment(ref inside) != 1)
            {
                Interlocked.Increment(ref violations);
            }
        }

        void LeaveCritical() => Interlocked.Decrement(ref inside);

        var synchronousThreads = Enumerable.Range(0, 3).Select(_ => new Thread(() =>
        {
            try
            {
                for (var n = 0; n < Rounds; n++)
                {
                    Assert.True(mutex.Enter());
                    EnterCritical();
                    var before = counter;
                    Thread.SpinWait(20);
                    counter = before + 1;
                    LeaveCritical();
                    mutex.Exit();
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        })
        {
            IsBackground = true,
        }).ToArray();

        var asynchronousTasks = Enumerable.Range(0, 3).Select(index => Task.Run(async () =>
        {
            for (var n = 0; n < Rounds; n++)
            {
                var acquired = index switch
                {
                    0 => await mutex.EnterAsync(),
                    1 => await mutex.EnterAsync(neverCanceled.Token),
                    _ => await mutex.EnterAsync(TimeSpan.FromSeconds(30)),
                };
                Assert.True(acquired);
                EnterCritical();
                var before = counter;
                await Task.Yield();
                counter = before + 1;
                LeaveCritical();
                mutex.Exit();
            }
        })).ToArray();

        foreach (var synchronousThread in synchronousThreads)
        {
            synchronousThread.Start();
        }

        await Task.WhenAll(asynchronousTasks).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var synchronousThread in synchronousThreads)
        {
            Assert.True(synchronousThread.Join(WaitTimeout));
        }

        Assert.Empty(failures);
        Assert.Equal(0, violations);
        Assert.Equal(6 * Rounds, counter);
        Assert.False(mutex.IsLocked);
        Assert.True(mutex.TryEnter());
        mutex.Exit();
    }

    private static async Task<(bool Acquired, bool ObservedInsideTrigger)> EnterAndObserveAsync(SemaphoreLock mutex, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // ConfigureAwait(false) lets the continuation run inline wherever the task completes, so an inline completion inside Cancel() or Exit() would be observed.
        var acquired = await mutex.EnterAsync(timeout, cancellationToken).ConfigureAwait(false);
        return (acquired, insideTrigger);
    }

    private static Thread StartRaceThread(Barrier barrier, int iterations, ConcurrentQueue<Exception> failures, Action<int> action)
    {
        var thread = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                if (!barrier.SignalAndWait(WaitTimeout))
                {
                    return;
                }

                try
                {
                    action(i);
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }

                if (!barrier.SignalAndWait(WaitTimeout))
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return thread;
    }

    private static void RunScopeCycles(SemaphoreLock mutex, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using (mutex.EnterScopeAsync().GetAwaiter().GetResult())
            {
            }
        }
    }

    private static void RunOnDedicatedThread(Action action)
    {
        // A dedicated thread guarantees a different thread from the caller, unlike a thread-pool work item.
        var thread = new Thread(() => action())
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(thread.Join(WaitTimeout));
    }

    private static bool IsBlocked(Thread thread)
        => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;

    private static void WaitUntilBlockedAgain(Thread thread)
    {
        // The interrupted thread leaves its wait and then blocks on the monitor held by the test.
        // Both waits report WaitSleepJoin and nothing else separates them, so a short sleep gives the thread time to handle the interruption first.
        // If the machine is too slow, the two interruptions coalesce into one; the assertions still hold, only the retry path goes untested.
        // A thread that gave up on the cleanup has ended; the caller's ownership assertions then report the stranded waiter.
        Thread.Sleep(100);
        Assert.True(SpinWait.SpinUntil(() => IsBlocked(thread) || !thread.IsAlive, WaitTimeout));
    }

    private sealed class CountingLock : IAsyncLockable
    {
        private readonly bool accept;
        private bool locked;

        public CountingLock(bool accept)
        {
            this.accept = accept;
        }

        public int EnterCalls { get; private set; }

        public int EnterAsyncCalls { get; private set; }

        public int ExitCalls { get; private set; }

        public bool IsLocked => this.locked;

        public bool Enter()
        {
            this.EnterCalls++;
            this.locked = this.accept;
            return this.accept;
        }

        public Task<bool> EnterAsync()
        {
            this.EnterAsyncCalls++;
            this.locked = this.accept;
            return Task.FromResult(this.accept);
        }

        public void Exit()
        {
            this.ExitCalls++;
            if (!this.locked)
            {
                throw new SynchronizationLockException();
            }

            this.locked = false;
        }
    }
}
