// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class ExecutionTests
{
    [Fact]
    public void InvalidConstructorsDoNotLeaveChildrenAndMovesObserveCancellation()
    {
        using var root = new ExecutionRoot();
        var count = root.Count;
        Assert.Throws<ArgumentNullException>(() => new TaskCore(root, null!));
        Assert.Throws<ArgumentNullException>(() => new ThreadCore(root, null!));
        Assert.Equal(count, root.Count);
        using var child = new TaskCompletionCore(root);
        using var stopped = new ExecutionGroup(root);
        stopped.RequestTermination();
        child.Parent = stopped;
        Assert.True(child.IsTerminated);
        child.Dispose();
        Assert.Throws<ObjectDisposedException>(() => child.Parent = root);
    }

    [Fact]
    public void TreeMembershipSnapshotsAndTokensStayConsistent()
    {
        using var root = new ExecutionRoot();
        using var otherRoot = new ExecutionRoot();
        using var group = new ExecutionGroup(root, name: "first");
        using var destination = new ExecutionGroup(root);
        using var child = new TaskCompletionCore(group);
        var snapshot = group.GetChildren();
        Assert.Same(snapshot, group.GetChildren());
        Assert.Same(child, Assert.Single(snapshot));
        Assert.Same(child, group.FindChild(child.Id));
        Assert.True(group.TryGetChildCancellationToken(child.Id, out var token));
        Assert.Same(child, token.ExtractCore());
        Assert.Same(child, token.Extract<TaskCompletionCore>());
        Assert.Null(token.Extract<ThreadCore>());
        Assert.Equal(token, child.Token);
        Assert.Equal(token, child.Pack());
        Assert.Null(CancellationToken.None.ExtractCore());
        Assert.Null(new CancellationToken(true).ExtractCore());
        Assert.Throws<InvalidOperationException>(() => group.Parent = group);
        Assert.Throws<InvalidOperationException>(() => root.BaseGroup.Parent = new ExecutionGroup(root.BaseGroup));
        Assert.Throws<InvalidOperationException>(() => child.Parent = otherRoot);
        Assert.Throws<ArgumentNullException>(() => group.AddChild(null!));
        destination.AddChild(child);
        Assert.Empty(group.GetChildren());
        Assert.Single(snapshot);
        Assert.Same(destination, child.Parent);
        Assert.False(group.TryGetChildCancellationToken(child.Id, out _));
        child.Parent = null;
        Assert.Empty(destination.GetChildren());
        Assert.Contains("first", group.ToString());
        Assert.Contains("Core", child.ToString());
    }

    [Fact]
    public void GroupsAndStacksRejectInconsistentOwnership()
    {
        using var root = new ExecutionRoot();
        using var other = new ExecutionRoot();
        var group = root.GetOrAddGroup(false, "unit");
        Assert.Same(group, root.GetOrAddGroup(false, "unit"));
        Assert.Throws<InvalidOperationException>(() => root.GetOrAddGroup(true, "unit"));
        Assert.Throws<ArgumentNullException>(() => root.GetOrAddGroup(false, null!));
        Assert.Same(root.UnitGroup("service"), root.UnitGroup("service"));
        var stack = new ExecutionStack(root);
        var secondStack = new ExecutionStack(root);
        Assert.True(stack.IsEmpty);
        Assert.Null(stack.FirstCore);
        Assert.Null(stack.LastCore);
        using var core = stack.PushNew(group);
        Assert.Equal(1, stack.Count);
        Assert.Same(core, stack.FirstCore);
        Assert.Same(core, stack.LastCore);
        Assert.Same(core, stack.Find(core.Id));
        Assert.Null(stack.Find(core.Id ^ 1));
        Assert.True(stack.Push(core));
        Assert.False(secondStack.Push(core));
        Assert.Throws<InvalidOperationException>(() => stack.Push(other));
        Assert.Throws<InvalidOperationException>(() => stack.PushNew(other));
        Assert.Throws<InvalidOperationException>(() => new TaskCompletionGroup(other, stack));
        core.TrySetCompleted();
        core.TrySetCompleted();
        Assert.True(core.CompletionTask.IsCompletedSuccessfully);
        core.Dispose();
        Assert.True(stack.IsEmpty);
        Assert.Null(core.Stack);
    }

    [Fact]
    public async Task CancellationIndependenceAndCompletedTasksHaveSeparateLifetimes()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        using var independent = new ExecutionGroup(group, true);
        using var normal = new TaskCompletionCore(group);
        using var separate = new TaskCompletionCore(independent);
        normal.TrySetCompleted();
        Assert.False(normal.IsTerminated);
        group.RequestTermination();
        Assert.False(normal.CanContinue);
        Assert.True(separate.CanContinue);
        Assert.True(await group.WaitForTermination(0));
        Assert.False(await group.WaitForTermination(0, TerminationOptions.IncludeIndependent));
        group.RequestTermination(TerminationOptions.IncludeIndependent);
        Assert.True(await group.WaitForTermination(0, TerminationOptions.IncludeIndependent));
        using var late = new TaskCore(group, _ => Task.CompletedTask);
        Assert.True(late.IsTerminated);
        using var plain = new ExecutionCore(root);
        plain.RequestTermination();
        Assert.True(plain.IsDisposed);
        Assert.Null(plain.Parent);
    }

    [Fact]
    public void DisposingGroupDetachesIndependentChildren()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        using var independent = new ExecutionGroup(group, true);
        group.Dispose();
        Assert.Null(independent.Parent);
        Assert.True(independent.CanContinue);
        Assert.Empty(group.GetChildren());
    }

    [Fact]
    public async Task DelayedTaskStartsOnceAndWaitsForItsActualExit()
    {
        using var root = new ExecutionRoot();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var core = new TaskCore(
            root,
            async _ =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task;
            },
            ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.KeepAliveOnCompletion);
        var zeroWait = core.WaitForTermination(0);
        Assert.True(zeroWait.IsCompletedSuccessfully);
        Assert.False(await zeroWait);
        root.SendSignal(ExecutionSignal.Start);
        Parallel.For(0, 30, _ => core.SendSignal(ExecutionSignal.Start));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        core.RequestTermination();
        Assert.False(core.IsTerminated);
        Assert.False(await core.WaitForTermination(0));
        release.SetResult();
        await core.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await core.WaitForTermination(1000));
        Assert.Equal(1, calls);
        Assert.False(core.IsDisposed);
    }

    [Fact]
    public async Task RootWaitIncludesBaseServicesButExcludesIndependentGroup()
    {
        using var root = new ExecutionRoot();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new TaskCore(root.BaseGroup, async _ =>
        {
            started.SetResult();
            await release.Task;
        });
        using var independent = new TaskCompletionCore(root.IndependentGroup);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(await root.WaitForTermination(0));
            Assert.False(service.CanContinue);
        }
        finally
        {
            release.SetResult();
        }

        await service.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await root.WaitForTermination(1000));
        Assert.True(independent.CanContinue);
        Assert.False(await root.WaitForTermination(0, TerminationOptions.IncludeIndependent));
    }

    [Fact]
    public async Task ThreadCoreSignalsAndCancellationBeforeStart()
    {
        using var root = new ExecutionRoot();
        var ran = 0;
        using var thread = new ThreadCore(root, _ => Interlocked.Increment(ref ran), ExecutionCoreOptions.DelayedStart);
        Assert.False(thread.IsTerminated);
        Parallel.For(0, 30, _ => thread.SendSignal(ExecutionSignal.Start));
        Assert.True(await thread.WaitForTermination(5000));
        Assert.Equal(1, ran);
        Assert.True(thread.IsDisposed);
        using var canceled = new ThreadCore(root, _ => Interlocked.Increment(ref ran), ExecutionCoreOptions.DelayedStart);
        canceled.RequestTermination();
        canceled.SendSignal(ExecutionSignal.Start);
        Assert.True(canceled.IsTerminated);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task DelaysAndWaitsHandleCancellationAndInvalidDurations()
    {
        using var root = new ExecutionRoot();
        using var core = new TaskCompletionCore(root);
        Assert.True(await core.Delay(0));
        Assert.True(await Task.TryDelay(0));
        Assert.True(await Task.TryDelay(TimeSpan.Zero));
        using var source = new CancellationTokenSource();
        var delay = core.Delay(Timeout.Infinite, source.Token);
        source.Cancel();
        Assert.False(await delay.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await core.Delay(0, source.Token));
        Assert.False(await core.WaitForTermination(cancellationToken: source.Token));
        Assert.False(await Task.TryDelay(1000, source.Token));
        Assert.False(await Task.TryDelay(TimeSpan.FromSeconds(1), source.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => core.Delay(-2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => core.WaitForTermination(-2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => root.WaitForTermination(-2));
        Assert.True(root.BaseGroup.CanContinue);
        core.RequestTermination();
        Assert.False(await core.Delay(0));
    }

    [Fact]
    public void ChildLookupDoesNotAllocate()
    {
        using var root = new ExecutionRoot();
        using var child = new TaskCompletionCore(root);
        for (var n = 0; n < 1000; n++)
        {
            root.FindChild(child.Id);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 0; n < 1000; n++)
        {
            root.FindChild(child.Id);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
