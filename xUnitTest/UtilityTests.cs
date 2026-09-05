// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

public class UtilityTests
{
    [Fact]
    public async Task ExecutionIdFlowsToChildrenButNotBackToParent()
    {
        var id = ExecutionId.Get();
        Assert.NotEqual(0, id);
        Assert.Equal(id, ExecutionId.Get());
        Assert.Equal(id, await Task.Run(ExecutionId.Get, TestContext.Current.CancellationToken));
        Task<long> isolated;
        using (ExecutionContext.SuppressFlow())
        {
            isolated = Task.Run(ExecutionId.Get, TestContext.Current.CancellationToken);
        }

        Assert.NotEqual(id, await isolated);
        Assert.Equal(id, ExecutionId.Get());
    }

    [Fact]
    public void AllocationEstimatesAndExceptionConstructors()
    {
        Assert.Equal(sizeof(long), EstimateSize.Struct<long>());
        Assert.True(EstimateSize.Class<object>() >= IntPtr.Size * 2);
        Assert.True(EstimateSize.Constructor(() => new byte[100]) >= 100);
        Assert.Throws<ArgumentNullException>(() => EstimateSize.Constructor(null!));
        var cause = new InvalidOperationException();
        Assert.NotNull(new PanicException());
        Assert.Equal("failure", new PanicException("failure").Message);
        Assert.Same(cause, new PanicException("failure", cause).InnerException);
    }

    [Fact]
    public void CancellationPoolClearsOldRegistrationsAndRejectsDisposedSources()
    {
        var source = CancellationTokenPool.Rent();
        var called = false;
        source.Token.Register(() => called = true);
        CancellationTokenPool.TryResetAndReturn(source);
        var next = CancellationTokenPool.Rent();
        next.Cancel();
        Assert.False(called);
        CancellationTokenPool.TryResetAndReturn(next);
        Assert.Throws<ObjectDisposedException>(() => next.TryReset());
        Assert.Throws<ObjectDisposedException>(() => CancellationTokenPool.TryResetAndReturn(next));
        Assert.Throws<ArgumentNullException>(() => CancellationTokenPool.TryResetAndReturn(null!));
    }

    [Fact]
    public void MicroSleepValidatesDurationAndDisposal()
    {
        using var sleep = new MicroSleep();
        Assert.NotEqual(MicroSleep.Mode.Disposed, sleep.CurrentMode);
        Assert.Throws<ArgumentOutOfRangeException>(() => sleep.Sleep(-1));
        sleep.Sleep(0);
        sleep.Sleep(100);
        sleep.Dispose();
        sleep.Dispose();
        Assert.Equal(MicroSleep.Mode.Disposed, sleep.CurrentMode);
        Assert.Throws<ObjectDisposedException>(() => sleep.Sleep(0));
    }

    [Fact]
    public async Task DefaultInterfaceScopesAcquireAndRelease()
    {
        IAsyncLockable mutex = new InterfaceLock();
        using (mutex.EnterScope())
        {
            Assert.True(mutex.IsLocked);
        }

        Assert.False(mutex.IsLocked);
        using (await mutex.EnterScopeAsync())
        {
            Assert.True(mutex.IsLocked);
        }

        Assert.False(mutex.IsLocked);
    }

    private sealed class InterfaceLock : IAsyncLockable
    {
        private readonly SemaphoreLock inner = new();

        public bool IsLocked => this.inner.IsLocked;

        public bool Enter() => this.inner.Enter();

        public Task<bool> EnterAsync() => this.inner.EnterAsync();

        public void Exit() => this.inner.Exit();
    }
}
