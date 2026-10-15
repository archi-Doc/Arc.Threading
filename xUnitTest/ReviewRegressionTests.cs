// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class ReviewRegressionTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public void DisposingGroupIsNotReturnedByLookupsWhileItsSubtreeIsCanceled()
    {
        using var root = new ExecutionRoot();
        var unit = root.GetOrAddUnitGroup("unit");
        var unitId = unit.Id;
        using var child = new ExecutionGroup(unit);
        ExecutionGroup? lookedUp = null;
        ExecutionCore? found = null;
        TaskCompletionCore? restarted = null;
        child.CancellationToken.Register(() =>
        {// Runs during unit.Dispose(); exceptions here would be swallowed by termination.
            found = root.IndependentGroup.FindChild(unitId);
            lookedUp = root.GetOrAddUnitGroup("unit");
            restarted = new TaskCompletionCore(lookedUp);
        });

        unit.Dispose();
        Assert.Null(found);
        Assert.NotNull(lookedUp);
        Assert.NotSame(unit, lookedUp);
        Assert.NotNull(restarted);
        Assert.True(restarted.CanContinue);
        Assert.Same(lookedUp, root.GetOrAddUnitGroup("unit"));
        Assert.False(child.CanContinue);
        Assert.Null(child.Parent);
        restarted.Dispose();
        lookedUp.Dispose();
    }

    [Fact]
    public void DerivedCoresUnderATerminatedParentAreNotDisposedBeforeTheirConstructorsRun()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        group.RequestTermination();

        // Dispose(bool) overrides dereference fields assigned by the derived constructors.
        var thread = new GuardedThreadCore(group);
        var task = new GuardedTaskCore(group);
        var worker = new GuardedWorker(group);
        foreach (var core in new ExecutionCore[] { thread, task, worker })
        {
            Assert.True(core.IsTerminated);
            Assert.False(core.IsDisposed);
            Assert.Same(group, core.Parent);
        }

        thread.Dispose();
        task.Dispose();
        worker.Dispose();
        Assert.True(thread.DisposedWithResource);
        Assert.True(task.DisposedWithResource);
        Assert.True(worker.DisposedWithResource);
        Assert.Empty(group.GetChildren());

        // Instances of the exact library types are still disposed by their constructors.
        var ran = 0;
        using var plainTask = new TaskCore(group, _ =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        });
        using var plainThread = new ThreadCore(group, _ => Interlocked.Increment(ref ran));
        using var plainWorker = new ReusableJobWorker<ReusableTaskJob>(group);
        Assert.True(plainTask.IsDisposed);
        Assert.True(plainThread.IsDisposed);
        Assert.True(plainWorker.IsDisposed);
        Assert.Empty(group.GetChildren());
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task DerivedDelayedCoreCanceledBeforeStartIsDisposedByALaterStart()
    {
        using var root = new ExecutionRoot();
        var core = new GuardedTaskCore(root, ExecutionCoreOptions.DelayedStart);
        core.RequestTermination();
        Assert.False(core.IsDisposed);
        core.SendSignal(ExecutionSignal.Start); // Not sent by a constructor, so the derived instance may be disposed.
        Assert.True(core.IsDisposed);
        Assert.True(core.DisposedWithResource);
        Assert.True(await core.WaitForTerminationAsync(WaitLimit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptOnTheCancelingThreadDoesNotLoseTheCancellationOrTheLock(bool finiteTimeout)
    {
        var mutex = new SemaphoreLock();
        Assert.True(mutex.Enter());
        using var source = new CancellationTokenSource();
        var waiter = finiteTimeout ? mutex.EnterAsync(TimeSpan.FromSeconds(30), source.Token) : mutex.EnterAsync(source.Token);
        Assert.False(waiter.IsCompleted);

        Exception? cancelException = null;
        var interruptObserved = false;
        var thread = new Thread(() =>
        {
            try
            {
                source.Cancel();
            }
            catch (Exception exception)
            {
                cancelException = exception;
            }

            try
            {// The suppressed interruption must still reach this thread at its next blocking call.
                Thread.Sleep(Timeout.Infinite);
            }
            catch (ThreadInterruptedException)
            {
                interruptObserved = true;
            }
        });

        lock (mutex)
        {// SemaphoreLock synchronizes on itself, so this blocks the canceling thread inside the lock's cancellation handling.
            thread.Start();
            Assert.True(SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, WaitLimit));
            thread.Interrupt();
            Thread.Sleep(100); // Gives the interruption time to hit the blocked monitor wait.
        }

        Assert.True(thread.Join(WaitLimit));
        Assert.Null(cancelException);
        Assert.True(interruptObserved);
        Assert.False(await waiter.WaitAsync(WaitLimit));
        Assert.True(mutex.IsLocked);
        mutex.Exit();
        Assert.False(mutex.IsLocked);
        Assert.True(mutex.TryEnter());
        mutex.Exit();
    }

    private sealed class GuardedThreadCore : ThreadCore
    {
        private readonly object resource;

        public GuardedThreadCore(ExecutionGroup parent)
            : base(parent, _ => { })
        {
            this.resource = new object();
        }

        public bool DisposedWithResource { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.DisposedWithResource = this.resource.GetHashCode() == this.resource.GetHashCode();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GuardedTaskCore : TaskCore<GuardedTaskCore>
    {
        private readonly object resource;

        public GuardedTaskCore(ExecutionGroup parent, ExecutionCoreOptions options = ExecutionCoreOptions.None)
            : base(parent, _ => Task.CompletedTask, options)
        {
            this.resource = new object();
        }

        public bool DisposedWithResource { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.DisposedWithResource = this.resource.GetHashCode() == this.resource.GetHashCode();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GuardedWorker : ReusableJobWorker<ReusableTaskJob>
    {
        private readonly object resource;

        public GuardedWorker(ExecutionGroup parent)
            : base(parent)
        {
            this.resource = new object();
        }

        public bool DisposedWithResource { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.DisposedWithResource = this.resource.GetHashCode() == this.resource.GetHashCode();
            }

            base.Dispose(disposing);
        }
    }
}
