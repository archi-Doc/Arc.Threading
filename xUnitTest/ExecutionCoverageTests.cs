// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using System.Diagnostics;
using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class ExecutionCoverageTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public void TryPushAttachesFreeExecutionsInOrderAndDisposalRemovesThem()
    {
        using var root = new ExecutionRoot();
        var stack = new ExecutionStack(root);
        var other = new ExecutionStack(root);
        using var first = new TaskCompletionCore(root);
        using var second = new ExecutionGroup(root);
        var secondId = second.Id;
        Assert.Null(first.Stack);
        Assert.Null(second.Stack);

        Assert.True(stack.TryPush(first));
        Assert.Same(stack, first.Stack);
        Assert.Equal(1, stack.Count);
        Assert.False(stack.IsEmpty);
        Assert.True(stack.TryPush(second));
        Assert.Same(stack, second.Stack);
        Assert.Equal(2, stack.Count);
        Assert.Same(first, stack.FirstCore);
        Assert.Same(second, stack.LastCore);
        Assert.Same(first, stack.Find(first.Id));
        Assert.Same(second, stack.Find(secondId));

        Assert.False(other.TryPush(first)); // Already on another stack.
        Assert.Same(stack, first.Stack);
        Assert.True(other.IsEmpty);
        Assert.True(stack.TryPush(first)); // Idempotent for the owning stack.
        Assert.Equal(2, stack.Count);
        Assert.Same(root, first.Parent); // Pushing does not change the tree membership.

        second.Dispose();
        Assert.Null(second.Stack);
        Assert.Equal(1, stack.Count);
        Assert.Same(first, stack.FirstCore);
        Assert.Same(first, stack.LastCore);
        Assert.Null(stack.Find(secondId));

        first.Dispose();
        Assert.True(stack.IsEmpty);
        Assert.Null(stack.FirstCore);
        Assert.Null(stack.LastCore);
    }

    [Fact]
    public async Task TaskCompletionCoreCompletesInEitherOrderIndependentlyOfTermination()
    {
        using var root = new ExecutionRoot();

        // Read, then complete.
        using var readFirst = new TaskCompletionCore(root);
        var task = readFirst.CompletionTask;
        Assert.False(task.IsCompleted);
        Assert.Same(task, readFirst.CompletionTask);
        readFirst.SetCompleted();
        Assert.True(task.IsCompletedSuccessfully);
        readFirst.SetCompleted();
        Assert.Same(task, readFirst.CompletionTask);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.True(readFirst.CanContinue); // Completion does not request termination.

        // Complete, then read.
        using var completeFirst = new TaskCompletionCore(root);
        using var completeFirst2 = new TaskCompletionCore(root);
        completeFirst.SetCompleted();
        completeFirst.SetCompleted();
        completeFirst2.SetCompleted();
        Assert.True(completeFirst.CompletionTask.IsCompletedSuccessfully);
        Assert.Same(completeFirst.CompletionTask, completeFirst.CompletionTask); // Created once on first read.
        Assert.NotSame(completeFirst.CompletionTask, completeFirst2.CompletionTask); // Each instance has its own task.
        await completeFirst.CompletionTask.WaitAsync(WaitLimit);

        // Termination and disposal do not complete the task; completion still works afterwards.
        using var terminated = new TaskCompletionCore(root);
        var pending = terminated.CompletionTask;
        terminated.RequestTermination();
        Assert.True(terminated.IsTerminated);
        Assert.False(pending.IsCompleted);
        terminated.Dispose();
        Assert.False(pending.IsCompleted);
        Assert.Same(pending, terminated.CompletionTask);
        terminated.SetCompleted();
        Assert.True(pending.IsCompletedSuccessfully);

        using var disposedUnread = new TaskCompletionCore(root);
        disposedUnread.Dispose();
        Assert.False(disposedUnread.CompletionTask.IsCompleted);

        // A continuation registered before SetCompleted does not run inline.
        using var core = new TaskCompletionCore(root);
        await AssertContinuationDoesNotRunInline(core.CompletionTask, core.SetCompleted);
    }

    [Fact]
    public async Task PushedTaskCompletionGroupCompletesInEitherOrderIndependentlyOfTermination()
    {
        using var root = new ExecutionRoot();
        var stack = new ExecutionStack(root);

        using var readFirst = stack.PushNew(root);
        var task = readFirst.CompletionTask;
        Assert.False(task.IsCompleted);
        readFirst.SetCompleted();
        readFirst.SetCompleted();
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Same(task, readFirst.CompletionTask);
        Assert.True(readFirst.CanContinue);

        using var completeFirst = stack.PushNew(root);
        using var completeFirst2 = stack.PushNew(root);
        completeFirst.SetCompleted();
        completeFirst.SetCompleted();
        completeFirst2.SetCompleted();
        Assert.True(completeFirst.CompletionTask.IsCompletedSuccessfully);
        Assert.Same(completeFirst.CompletionTask, completeFirst.CompletionTask);
        Assert.NotSame(completeFirst.CompletionTask, completeFirst2.CompletionTask);
        await completeFirst.CompletionTask.WaitAsync(WaitLimit);

        using var terminated = stack.PushNew(root);
        var pending = terminated.CompletionTask;
        terminated.RequestTermination();
        Assert.True(terminated.IsTerminated);
        Assert.False(pending.IsCompleted);
        terminated.Dispose();
        Assert.Null(terminated.Stack);
        Assert.False(pending.IsCompleted);
        terminated.SetCompleted();
        Assert.True(pending.IsCompletedSuccessfully);

        using var group = stack.PushNew(root);
        await AssertContinuationDoesNotRunInline(group.CompletionTask, group.SetCompleted);
    }

    [Fact]
    public void ParentSetterIgnoresSameParentAndRootAndReattachedExecutionsObserveTheNewParent()
    {
        using var root = new ExecutionRoot();
        using var otherRoot = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        using var child = new TaskCompletionCore(group);

        // Same parent: no-op, the cached snapshot survives.
        var snapshot = group.GetChildren();
        child.Parent = group;
        group.AddChild(child);
        Assert.Same(snapshot, group.GetChildren());
        Assert.Same(group, child.Parent);
        Assert.Equal(1, group.ChildCount);

        // The parent of the root cannot be set.
        var rootChildren = root.BaseGroup.GetChildren();
        root.Parent = root.BaseGroup;
        Assert.Null(root.Parent);
        root.Parent = root;
        Assert.Null(root.Parent);
        root.Parent = otherRoot;
        Assert.Null(root.Parent);
        otherRoot.BaseGroup.AddChild(root);
        Assert.Null(root.Parent);
        root.Parent = null;
        Assert.Null(root.Parent);
        Assert.Same(rootChildren, root.BaseGroup.GetChildren());
        Assert.DoesNotContain(root, otherRoot.BaseGroup.GetChildren());
        Assert.True(root.CanContinue);

        // Detach, then re-attach under another parent.
        using var source = new ExecutionGroup(root);
        using var destination = new ExecutionGroup(root);
        using var mover = new TaskCompletionCore(source);
        var id = mover.Id;
        mover.Parent = null;
        Assert.Null(mover.Parent);
        Assert.DoesNotContain(mover, source.GetChildren());
        Assert.Equal(id, mover.Id); // Only disposal resets the identifier.
        source.RequestTermination();
        Assert.True(mover.CanContinue); // A detached execution does not receive termination.

        mover.Parent = destination;
        Assert.Same(destination, mover.Parent);
        Assert.Contains(mover, destination.GetChildren());
        Assert.True(mover.CanContinue);
        destination.RequestTermination();
        Assert.False(mover.CanContinue);
        Assert.True(mover.IsTerminated);
    }

    [Fact]
    public async Task GroupDisposalCancelsAndDetachesDescendantsButOnlyDetachesIndependentChildren()
    {
        using var root = new ExecutionRoot();
        var stack = new ExecutionStack(root);
        using var group = new ExecutionGroup(root);
        using var nested = new ExecutionGroup(group);
        using var leaf = new TaskCompletionCore(nested);
        using var pushed = stack.PushNew(group);
        using var pushedChild = new TaskCompletionCore(pushed);
        using var independentLeaf = new TaskCompletionCore(group) { IsIndependent = true };
        using var independentGroup = new ExecutionGroup(group, true);
        var started = NewSignal();
        using var independentTask = new TaskCore(independentGroup, async core =>
        {
            started.SetResult();
            await core.TryDelay(Timeout.Infinite);
        });
        await started.Task.WaitAsync(WaitLimit);
        var completion = pushed.CompletionTask;
        var independentLeafId = independentLeaf.Id;

        group.Dispose();

        Assert.True(group.IsDisposed);
        Assert.Null(group.Parent);
        Assert.Equal(0, group.Id);
        Assert.DoesNotContain(group, root.GetChildren());
        Assert.Empty(group.GetChildren());
        foreach (var x in new ExecutionCore[] { nested, leaf, pushed, pushedChild })
        {
            Assert.True(x.IsTerminated);
            Assert.False(x.CanContinue);
            Assert.Null(x.Parent);
            Assert.Equal(0, x.Id);
            Assert.Null(x.Stack);
            Assert.False(x.IsDisposed);
        }

        Assert.Empty(nested.GetChildren());
        Assert.Empty(pushed.GetChildren());
        Assert.True(stack.IsEmpty);
        Assert.False(completion.IsCompleted);

        Assert.True(independentLeaf.CanContinue);
        Assert.Null(independentLeaf.Parent);
        Assert.Equal(independentLeafId, independentLeaf.Id);
        Assert.False(independentLeaf.IsDisposed);
        Assert.True(independentGroup.CanContinue);
        Assert.Null(independentGroup.Parent);
        Assert.Same(independentGroup, independentTask.Parent);
        Assert.True(independentTask.CanContinue);
        Assert.False(independentTask.Task.IsCompleted);

        independentGroup.RequestTermination();
        await independentTask.Task.WaitAsync(WaitLimit);
        Assert.True(independentTask.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void GroupDisposalTerminatesWorkAttachedToADescendantByACancellationCallback()
    {
        using var root = new ExecutionRoot();
        using var outer = new ExecutionGroup(root);
        using var inner = new ExecutionGroup(outer);
        using var first = new TaskCompletionCore(inner);
        TaskCompletionCore? late = null;
        using var registration = first.Token.Register(() => late = new TaskCompletionCore(inner));
        try
        {
            outer.Dispose();
            Assert.NotNull(late);
            Assert.True(late.IsTerminated);
            Assert.Null(late.Parent);
            Assert.Equal(0, late.Id);
            Assert.True(first.IsTerminated);
            Assert.True(inner.IsTerminated);
            Assert.Null(inner.Parent);
            Assert.Empty(inner.GetChildren());
        }
        finally
        {
            late?.Dispose();
        }
    }

    [Fact]
    public void GroupDisposalTerminatesWorkMovedUnderADescendantByACancellationCallback()
    {
        using var root = new ExecutionRoot();
        using var outer = new ExecutionGroup(root);
        using var inner = new ExecutionGroup(outer);
        using var first = new TaskCompletionCore(inner);
        using var moved = new TaskCompletionCore(root);
        using var registration = first.Token.Register(() => moved.Parent = inner);

        outer.Dispose();

        Assert.True(moved.IsTerminated);
        Assert.Null(moved.Parent);
        Assert.Equal(0, moved.Id);
        Assert.DoesNotContain(moved, root.GetChildren());
        Assert.Null(inner.Parent);
        Assert.Empty(inner.GetChildren());
    }

    [Fact]
    public async Task WaitForTerminationReturnsFalseWhenTheTokenIsCanceledWhilePending()
    {
        using var root = new ExecutionRoot();
        using var core = new TaskCompletionCore(root);
        using var group = new ExecutionGroup(root);
        using var child = new TaskCompletionCore(group);
        using var source = new CancellationTokenSource();
        var wait = core.WaitForTerminationAsync(Timeout.InfiniteTimeSpan, TerminationOptions.None, source.Token);
        var groupWait = group.WaitForTerminationAsync(TimeSpan.FromSeconds(30), TerminationOptions.None, source.Token);
        Assert.False(wait.IsCompleted);
        Assert.False(groupWait.IsCompleted);
        source.Cancel();
        Assert.False(await wait.WaitAsync(WaitLimit));
        Assert.False(await groupWait.WaitAsync(WaitLimit));
        Assert.True(core.CanContinue);
        Assert.True(child.CanContinue);
    }

    [Fact]
    public async Task FiniteWaitWithASubIntervalRemainderDoesNotSpin()
    {
        using var root = new ExecutionRoot();
        using var core = new CountingCore(root);
        var stopwatch = Stopwatch.StartNew();
        Assert.False(await core.WaitForTerminationAsync(TimeSpan.FromMilliseconds(17)).WaitAsync(WaitLimit));
        var elapsed = stopwatch.Elapsed;
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(16), $"Elapsed: {elapsed}");
        Assert.True(core.Calls > 0);
        Assert.True(core.Calls < 100, $"IsTerminated was polled {core.Calls} times.");
    }

    [Fact]
    public async Task WaitOnTaskCoreReturnsTrueSoonAfterItsDelegateExits()
    {
        using var root = new ExecutionRoot();
        var release = NewSignal();
        using var core = new TaskCore(root, _ => release.Task);
        var wait = core.WaitForTerminationAsync(TimeSpan.FromSeconds(30));
        Assert.False(wait.IsCompleted);
        var stopwatch = Stopwatch.StartNew();
        release.SetResult();
        Assert.True(await wait.WaitAsync(WaitLimit));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Elapsed: {stopwatch.Elapsed}");
        Assert.True(core.IsTerminated);
        Assert.True(core.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TryDelayWithAnAdditionalTokenElapsesOrObservesTermination()
    {
        using var root = new ExecutionRoot();
        using var external = new CancellationTokenSource();

        using var core = new TaskCompletionCore(root);
        Assert.True(await core.TryDelay(TimeSpan.FromMilliseconds(20), external.Token).WaitAsync(WaitLimit));
        Assert.True(await core.TryDelay(20, external.Token).WaitAsync(WaitLimit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => core.TryDelay(TimeSpan.MaxValue));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => core.TryDelay(TimeSpan.MaxValue, external.Token));
        Assert.True(core.CanContinue);

        // Termination of the core cancels a delay linked with the additional token.
        var linked = core.TryDelay(Timeout.InfiniteTimeSpan, external.Token);
        Assert.False(linked.IsCompleted);
        core.RequestTermination();
        Assert.False(await linked.WaitAsync(WaitLimit));
        Assert.False(external.IsCancellationRequested);

        // The core's own token passed as the additional token.
        using var self = new TaskCompletionCore(root);
        var delay = self.TryDelay(Timeout.Infinite, self.CancellationToken);
        Assert.False(delay.IsCompleted);
        self.RequestTermination();
        Assert.False(await delay.WaitAsync(WaitLimit));
        Assert.False(await self.TryDelay(10, self.CancellationToken));
    }

    [Fact]
    public void CustomSignalHandlersReplaceTheOverrideAndPlainCoresIgnoreSignals()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        ExecutionCore? target = null;
        var received = new List<ExecutionSignal>();
        using var handled = new SignalCore(group, (core, signal) =>
        {
            target = core;
            received.Add(signal);
        });
        using var unhandled = new SignalCore(group, null);
        using var plain = new ExecutionCore(group);

        group.SendSignal(ExecutionSignal.Start);
        group.SendSignal(ExecutionSignal.Cancel);
        group.SendSignal(ExecutionSignal.Terminate);

        Assert.Same(handled, target);
        Assert.Equal(new[] { ExecutionSignal.Start, ExecutionSignal.Cancel, ExecutionSignal.Terminate }, received);
        Assert.Equal(0, handled.OverrideCalls);
        Assert.Equal(3, unhandled.OverrideCalls);

        Assert.True(plain.CanContinue);
        Assert.False(plain.IsDisposed);
        Assert.Same(group, plain.Parent);
        Assert.True(handled.CanContinue);
        Assert.True(unhandled.CanContinue);
    }

    [Fact]
    public async Task GroupForwardsSignalsToAllChildrenAndRethrowsHandlerExceptions()
    {
        using var root = new ExecutionRoot();

        // A single failure is rethrown as is, after the remaining children received the signal.
        using var group = new ExecutionGroup(root);
        var failure = new InvalidOperationException("Handler failure");
        using var throwing = new ExecutionCore(group, (_, _) => throw failure);
        var ran = 0;
        using var delayed = new TaskCore(
            group,
            _ =>
            {
                Interlocked.Increment(ref ran);
                return Task.CompletedTask;
            },
            ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.NoDisposeOnCompletion);
        var thrown = Assert.Throws<InvalidOperationException>(() => group.SendSignal(ExecutionSignal.Start));
        Assert.Same(failure, thrown);
        await delayed.Task.WaitAsync(WaitLimit);
        Assert.True(delayed.Task.IsCompletedSuccessfully);
        Assert.Equal(1, ran);
        Assert.False(delayed.IsDisposed);

        // Several failures are aggregated in child order.
        using var group2 = new ExecutionGroup(root);
        var calls = 0;
        using var first = new ExecutionCore(group2, (_, _) => throw new InvalidOperationException("First"));
        using var middle = new ExecutionCore(group2, (_, _) => calls++);
        using var last = new ExecutionCore(group2, (_, _) => throw new ArgumentException("Last"));
        var aggregate = Assert.Throws<AggregateException>(() => group2.SendSignal(ExecutionSignal.Cancel));
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.IsType<InvalidOperationException>(aggregate.InnerExceptions[0]);
        Assert.IsType<ArgumentException>(aggregate.InnerExceptions[1]);
        Assert.Equal(1, calls);
        Assert.True(first.CanContinue);
        Assert.True(last.CanContinue);
    }

    [Fact]
    public async Task ReadmeQuickStartTerminatesWorkersUnderTheRootBaseAndUnitGroups()
    {
        using var root = new ExecutionRoot();
        var unit = root.GetOrAddUnitGroup("unit");
        var ticked = new[] { NewSignal(), NewSignal(), NewSignal() };
        using var worker = new TaskCore(root, async core =>
        {
            while (await core.TryDelay(10))
            {
                ticked[0].TrySetResult();
            }
        });
        using var service = new TaskCore(root.BaseGroup, async core =>
        {
            while (await core.TryDelay(10))
            {
                ticked[1].TrySetResult();
            }
        });
        using var unitWorker = new TaskCore(unit, async core =>
        {
            while (await core.TryDelay(10))
            {
                ticked[2].TrySetResult();
            }
        });

        await Task.WhenAll(ticked.Select(x => x.Task)).WaitAsync(WaitLimit);
        root.RequestTermination(TerminationOptions.IncludeIndependent);
        Assert.True(await root.WaitForTerminationAsync(WaitLimit, TerminationOptions.IncludeIndependent));

        foreach (var core in new[] { worker, service, unitWorker })
        {
            Assert.True(core.IsTerminated);
            Assert.False(core.CanContinue);
            Assert.True(core.IsDisposed);
            Assert.Null(core.Parent);
            Assert.Equal(0, core.Id);
        }

        await Task.WhenAll(worker.Task, service.Task, unitWorker.Task).WaitAsync(WaitLimit);
        Assert.True(worker.Task.IsCompletedSuccessfully);
        Assert.True(service.Task.IsCompletedSuccessfully);
        Assert.True(unitWorker.Task.IsCompletedSuccessfully);
        Assert.Empty(unit.GetChildren());
        Assert.Empty(root.BaseGroup.GetChildren());
    }

    [Fact]
    public void RequestTerminationAfterDisposeAndInheritedCancelDoNotTraverseOrDispose()
    {
        using var root = new ExecutionRoot();

        // RequestTermination after Dispose is a no-op.
        var group = new ExecutionGroup(root);
        var core = new TaskCompletionCore(root);
        var plainDisposed = new ExecutionCore(root);
        group.Dispose();
        core.Dispose();
        plainDisposed.Dispose();
        group.RequestTermination();
        group.RequestTermination(TerminationOptions.IncludeIndependent);
        core.RequestTermination();
        core.RequestTermination(TerminationOptions.IncludeIndependent);
        plainDisposed.RequestTermination();
        Assert.True(group.IsDisposed);
        Assert.True(core.IsDisposed);
        Assert.True(plainDisposed.IsDisposed);

        // Inherited Cancel() cancels only the target.
        using var group2 = new ExecutionGroup(root);
        using var child = new TaskCompletionCore(group2);
        using var plain = new ExecutionCore(group2);
        group2.Cancel();
        Assert.True(group2.IsCancellationRequested);
        Assert.True(group2.IsTerminated);
        Assert.True(child.CanContinue);
        Assert.True(plain.CanContinue);

        // A plain ExecutionCore canceled directly is not disposed automatically.
        var plainId = plain.Id;
        plain.Cancel();
        Assert.True(plain.IsTerminated);
        Assert.False(plain.IsDisposed);
        Assert.Same(group2, plain.Parent);
        Assert.Equal(plainId, plain.Id);
        Assert.Contains(plain, group2.GetChildren());
    }

    [Fact]
    public async Task DisposingTheRootTerminatesBaseIndependentAndUnitServices()
    {
        using var root = new ExecutionRoot();
        var baseGroup = root.BaseGroup;
        var unit = root.GetOrAddUnitGroup("unit");
        using var baseCore = new TaskCompletionCore(baseGroup);
        using var independentCore = new TaskCompletionCore(root.IndependentGroup);
        using var unitCore = new TaskCompletionCore(unit);
        var started = new[] { NewSignal(), NewSignal(), NewSignal() };
        using var baseService = new TaskCore(baseGroup, async core =>
        {
            started[0].SetResult();
            await core.TryDelay(Timeout.Infinite);
        });
        using var independentService = new TaskCore(root.IndependentGroup, async core =>
        {
            started[1].SetResult();
            await core.TryDelay(Timeout.Infinite);
        });
        using var unitService = new TaskCore(unit, async core =>
        {
            started[2].SetResult();
            await core.TryDelay(Timeout.Infinite);
        });
        await Task.WhenAll(started.Select(x => x.Task)).WaitAsync(WaitLimit);

        root.Dispose();

        Assert.True(root.IsDisposed);
        foreach (var x in new ExecutionCore[] { baseGroup, root.IndependentGroup, unit, baseCore, independentCore, unitCore, baseService, independentService, unitService })
        {
            Assert.False(x.CanContinue);
        }

        await Task.WhenAll(baseService.Task, independentService.Task, unitService.Task).WaitAsync(WaitLimit);

        // New work under the (terminated, detached) base group is terminated immediately.
        using var lateCore = new TaskCompletionCore(baseGroup);
        Assert.True(lateCore.IsTerminated);
        var ran = 0;
        using var lateTask = new TaskCore(baseGroup, _ =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        });
        Assert.True(lateTask.IsTerminated);
        Assert.True(lateTask.IsDisposed);
        Assert.Null(lateTask.Parent);
        Assert.Equal(0, ran);
    }

    [Fact]
    public void DefaultIdsAreUniqueNonzeroAndResetOnDisposal()
    {
        using var root = new ExecutionRoot();
        using var otherRoot = new ExecutionRoot();
        var cores = new ExecutionCore[1000];
        Parallel.For(0, cores.Length, i => cores[i] = (i % 2 == 0) ? new TaskCompletionCore(root) : new ExecutionGroup(root));
        var ids = new HashSet<int>(cores.Select(x => x.Id));
        Assert.Equal(cores.Length, ids.Count);
        Assert.DoesNotContain(0, ids);
        Assert.Equal(0, root.Id);
        Assert.NotEqual(0, root.BaseGroup.Id);
        Assert.NotEqual(0, root.IndependentGroup.Id);
        Assert.NotEqual(root.BaseGroup.Id, root.IndependentGroup.Id);
        Assert.DoesNotContain(root.BaseGroup.Id, ids);

        using var otherCore = new TaskCompletionCore(otherRoot);
        Assert.NotEqual(0, otherCore.Id);
        Assert.DoesNotContain(otherCore.Id, ids); // Unique per process, not only per root.
        Assert.NotEqual(otherRoot.BaseGroup.Id, root.BaseGroup.Id);

        foreach (var x in cores)
        {
            x.Dispose();
        }

        Assert.All(cores, x => Assert.Equal(0, x.Id));
        Assert.Equal(2, root.ChildCount);
    }

    [Fact]
    public void GroupToStringGetOrAddGroupAndTerminationOptionsPinDocumentedBehavior()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        Assert.StartsWith("Group(0)", group.ToString());
        using var first = new TaskCompletionCore(group);
        using var second = new TaskCompletionCore(group);
        var text = group.ToString();
        Assert.StartsWith("Group(", text);
        Assert.StartsWith("Group(2)", text);
        using var named = new ExecutionGroup(root, name: "named");
        Assert.StartsWith("named(0)", named.ToString());

        // GetOrAddGroup returns an existing group even when it is terminated.
        var stopped = root.GetOrAddGroup(false, "stopped");
        stopped.RequestTermination();
        Assert.Same(stopped, root.GetOrAddGroup(false, "stopped"));
        using var late = new TaskCompletionCore(stopped);
        Assert.True(late.IsTerminated);
        stopped.Dispose();
        var fresh = root.GetOrAddGroup(false, "stopped");
        Assert.NotSame(stopped, fresh);
        Assert.True(fresh.CanContinue);

        var unit = root.GetOrAddUnitGroup("unit");
        unit.RequestTermination();
        Assert.Same(unit, root.GetOrAddUnitGroup("unit"));
        Assert.True(unit.IsIndependent);

        Assert.Equal(default, TerminationOptions.None);
        Assert.Equal(0, (int)TerminationOptions.None);
        Assert.Equal(TerminationOptions.IncludeIndependent, TerminationOptions.None | TerminationOptions.IncludeIndependent);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertContinuationDoesNotRunInline(Task task, Action setCompleted)
    {
        using var insideSetCompleted = new ThreadLocal<bool>();
        var ranInline = true;
        var continuation = task.ContinueWith(
            _ => ranInline = insideSetCompleted.Value,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Assert.False(continuation.IsCompleted);

        insideSetCompleted.Value = true;
        try
        {
            setCompleted();
        }
        finally
        {
            insideSetCompleted.Value = false;
        }

        Assert.True(task.IsCompletedSuccessfully);
        await continuation.WaitAsync(WaitLimit);
        Assert.False(ranInline);
    }

    private sealed class CountingCore : ExecutionCore
    {
        private int calls;

        public CountingCore(ExecutionGroup parent)
            : base(parent)
        {
        }

        public int Calls => Volatile.Read(ref this.calls);

        public override bool IsTerminated
        {
            get
            {
                Interlocked.Increment(ref this.calls);
                return false;
            }
        }
    }

    private sealed class SignalCore : ExecutionCore
    {
        private int overrideCalls;

        public SignalCore(ExecutionGroup parent, ExecutionSignalHandler? signalHandler)
            : base(parent, signalHandler)
        {
        }

        public int OverrideCalls => Volatile.Read(ref this.overrideCalls);

        public override void OnSignalReceived(ExecutionSignal signal)
            => Interlocked.Increment(ref this.overrideCalls);
    }
}
