// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class LockTests
{
    [Fact]
    public async Task ScopeAndTryEnterReleaseExactlyOnce()
    {
        var mutex = new SemaphoreLock();
        Assert.Throws<SynchronizationLockException>(mutex.Exit);
        var scope = mutex.EnterScope();
        Assert.Same(mutex, scope.LockableObject);
        Assert.True(scope.IsLocked);
        Assert.True(mutex.IsLocked);
        Assert.False(mutex.TryEnter());
        Assert.False(await mutex.EnterAsync(0));
        scope.Dispose();
        scope.Dispose();
        Assert.False(mutex.IsLocked);
        using (await mutex.EnterScopeAsync())
        {
            Assert.True(mutex.IsLocked);
        }

        Assert.True(mutex.TryEnter());
        mutex.Exit();
    }

    [Fact]
    public async Task CancellationDoesNotAcquireOrStrandTheLock()
    {
        var mutex = new SemaphoreLock();
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.False(await mutex.EnterAsync(source.Token));
        Assert.False(mutex.IsLocked);
        Assert.True(mutex.Enter());
        using var waitingSource = new CancellationTokenSource();
        var canceled = mutex.EnterAsync(waitingSource.Token);
        var next = mutex.EnterAsync();
        waitingSource.Cancel();
        Assert.False(await canceled.WaitAsync(TimeSpan.FromSeconds(5)));
        mutex.Exit();
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        mutex.Exit();
        Assert.False(mutex.IsLocked);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-0.5)]
    [InlineData(4294967295)]
    public async Task InvalidTimeoutNeverChangesTheQueue(double milliseconds)
    {
        var mutex = new SemaphoreLock();
        var timeout = TimeSpan.FromMilliseconds(milliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = mutex.EnterAsync(timeout); });
        Assert.True(mutex.TryEnter());
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = mutex.EnterAsync(timeout); });
        mutex.Exit();
        Assert.True(await mutex.EnterAsync(TimeSpan.Zero));
        mutex.Exit();
    }

    [Fact]
    public async Task TimeoutRemovesWaiterAndMixedContentionPreservesExclusion()
    {
        var mutex = new SemaphoreLock();
        mutex.Enter();
        Assert.False(await mutex.EnterAsync(TimeSpan.FromMilliseconds(5)));
        mutex.Exit();
        var value = 0;
        var tasks = Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
        {
            for (var n = 0; n < 100; n++)
            {
                if (index % 2 == 0)
                {
                    using (mutex.EnterScope())
                    {
                        value++;
                    }
                }
                else
                {
                    using (await mutex.EnterScopeAsync())
                    {
                        var before = value;
                        await Task.Yield();
                        value = before + 1;
                    }
                }
            }
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1200, value);
        Assert.False(mutex.IsLocked);
    }

    [Fact]
    public void MonitorScopesAreReentrant()
    {
        var mutex = new MonitorLock();
        Assert.False(mutex.IsLocked);
        using (mutex.EnterScope())
        {
            using (mutex.EnterScope())
            {
                Assert.True(mutex.IsLocked);
            }

            Assert.True(mutex.IsLocked);
        }

        Assert.False(mutex.IsLocked);
        Assert.Throws<SynchronizationLockException>(mutex.Exit);
        default(LockStruct).Dispose();
    }
}
