// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class ExecutionRegressionTests
{
    [Fact]
    public void DisposedParentsRejectNewExecutionsWithoutChangingOwnership()
    {
        using var root = new ExecutionRoot();
        using var disposedParent = new ExecutionGroup(root);
        using var child = new TaskCompletionCore(root);
        var stack = new ExecutionStack(root);
        disposedParent.Dispose();
        var count = root.ChildCount;

        Assert.Throws<ObjectDisposedException>(() => new ExecutionCore(disposedParent));
        Assert.Throws<ObjectDisposedException>(() => new ExecutionGroup(disposedParent));
        Assert.Throws<ObjectDisposedException>(() => new TaskCore(disposedParent, _ => Task.CompletedTask));
        Assert.Throws<ObjectDisposedException>(() => new ThreadCore(disposedParent, _ => { }));
        Assert.Throws<ObjectDisposedException>(() => stack.PushNew(disposedParent));
        Assert.Throws<ObjectDisposedException>(() => child.Parent = disposedParent);
        Assert.Throws<ObjectDisposedException>(() => disposedParent.GetOrAddGroup(false, "late"));

        Assert.Same(root, child.Parent);
        Assert.Contains(child, root.GetChildren());
        Assert.Equal(count, root.ChildCount);
        Assert.Empty(disposedParent.GetChildren());
        Assert.True(stack.IsEmpty);
        Assert.True(child.CanContinue);
    }

    [Fact]
    public void TerminationContinuesAfterCallbackFailuresAndIncludesChildrenAddedByCallbacks()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        using var first = new TaskCompletionCore(group);
        using var second = new TaskCompletionCore(group);
        TaskCompletionCore? late = null;
        var callbacks = 0;
        using var firstRegistration = first.Token.Register(() =>
        {
            late = new TaskCompletionCore(group);
            throw new InvalidOperationException("Callback failure");
        });
        using var secondRegistration = second.Token.Register(() => callbacks++);

        try
        {
            group.RequestTermination();
            Assert.True(first.IsTerminated);
            Assert.True(second.IsTerminated);
            Assert.NotNull(late);
            Assert.True(late.IsTerminated);
            Assert.Equal(1, callbacks);
            group.RequestTermination();
            Assert.Equal(1, callbacks);
        }
        finally
        {
            late?.Dispose();
        }
    }

    [Fact]
    public void SignalsReachIndependentChildrenAndCustomGroupHandlers()
    {
        using var root = new ExecutionRoot();
        using var independent = new ExecutionGroup(root, true);
        var received = new List<ExecutionSignal>();
        using var child = new ExecutionCore(independent, (_, signal) => received.Add(signal));
        root.SendSignal(ExecutionSignal.Cancel);
        root.SendSignal(ExecutionSignal.Terminate);
        Assert.Equal(new[] { ExecutionSignal.Cancel, ExecutionSignal.Terminate }, received);
        Assert.True(child.CanContinue);

        var stack = new ExecutionStack(root);
        var groupSignals = 0;
        using var group = stack.PushNew(root, (_, _) => groupSignals++);
        using var delayed = new TaskCore(group, _ => Task.CompletedTask, ExecutionCoreOptions.DelayedStart);
        group.SendSignal(ExecutionSignal.Start);
        Assert.Equal(1, groupSignals);
        Assert.Equal(TaskStatus.Created, delayed.Task.Status);
    }

#if DEBUG
    [Fact(Skip = "Allocation assertions require optimized async state machines in a Release build.")]
#else
    [Fact]
#endif
    public async Task ZeroDelayAndAlreadyCanceledWaitDoNotAllocate()
    {
        using var root = new ExecutionRoot();
        using var core = new TaskCompletionCore(root);
        using var additional = new CancellationTokenSource();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        for (var i = 0; i < 1000; i++)
        {
            _ = core.TryDelay(0, additional.Token);
            _ = core.WaitForTerminationAsync(cancellationToken: canceled.Token);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _ = core.TryDelay(0, additional.Token);
            _ = core.WaitForTerminationAsync(cancellationToken: canceled.Token);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(await core.TryDelay(0, additional.Token));
        Assert.False(await core.WaitForTerminationAsync(cancellationToken: canceled.Token));
    }

    [Fact]
    public async Task FiniteWaitTimesOutWhileExecutionRemainsActive()
    {
        using var root = new ExecutionRoot();
        using var core = new TaskCompletionCore(root);
        Assert.False(await core.WaitForTerminationAsync(TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(core.CanContinue);
    }

    [Fact]
    public async Task FaultedTaskDisposesItsCoreAndPreservesTheException()
    {
        using var root = new ExecutionRoot();
        var failure = new InvalidOperationException("Delegate failure");
        using var core = new TaskCore(root, _ => Task.FromException(failure), ExecutionCoreOptions.DelayedStart);
        core.SendSignal(ExecutionSignal.Cancel);
        core.SendSignal(ExecutionSignal.Terminate);
        Assert.True(core.CanContinue);
        Assert.Equal(TaskStatus.Created, core.Task.Status);
        core.SendSignal(ExecutionSignal.Start);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => core.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, observed);
        Assert.True(core.IsTerminated);
        Assert.True(core.IsDisposed);
        Assert.Null(core.Parent);
    }

    [Fact]
    public async Task TypedTaskReceivesItselfAndInvalidSelfTypesLeaveNoChildren()
    {
        using var root = new ExecutionRoot();
        var count = root.ChildCount;
        Assert.Throws<InvalidOperationException>(() => new TaskCore<TypedTaskCore>(root, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new TypedTaskCore(root, null!));
        Assert.Equal(count, root.ChildCount);

        TypedTaskCore? received = null;
        using var core = new TypedTaskCore(root, current =>
        {
            received = current;
            return Task.CompletedTask;
        });
        core.SendSignal(ExecutionSignal.Start);
        await core.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(core, received);
        Assert.True(core.IsDisposed);
    }

    [Fact]
    public async Task DerivedTasksCanInitializeOnceUsingTheParameterlessDelegateFactory()
    {
        using var root = new ExecutionRoot();
        using var core = new ManualTaskCore(root);
        Assert.Throws<InvalidOperationException>(() => { _ = core.Task; });
        Assert.Throws<ArgumentNullException>(() => core.InitializeTask(null!));
        Assert.Throws<ArgumentNullException>(() => core.CreateTask(null!));

        var calls = 0;
        core.CreateTask(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        var task = core.Task;
        Assert.Throws<InvalidOperationException>(() => core.InitializeTask(Task.CompletedTask));
        Assert.Same(task, core.Task);
        core.SendSignal(ExecutionSignal.Start);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.True(core.IsTerminated);
    }

    private sealed class TypedTaskCore : TaskCore<TypedTaskCore>
    {
        public TypedTaskCore(ExecutionGroup parent, Func<TypedTaskCore, Task> method)
            : base(parent, method, ExecutionCoreOptions.DelayedStart)
        {
        }
    }

    private sealed class ManualTaskCore : TaskCore
    {
        public ManualTaskCore(ExecutionGroup parent)
            : base(parent, ExecutionCoreOptions.DelayedStart)
        {
        }

        public void CreateTask(Func<Task> method)
            => this.Initialize(this.CreateLongRunningTask(this, method));

        public void InitializeTask(Task task)
            => this.Initialize(task);
    }
}
