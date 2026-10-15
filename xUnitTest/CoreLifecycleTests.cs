// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class CoreLifecycleTests
{
    private const int RaceIterations = 500;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ThreadCoreWithDefaultOptionsStartsImmediatelyRunsOnceAndDisposesItself()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        var calls = 0;
        var idInside = 0;
        ThreadCore? received = null;
        var core = new ThreadCore(group, c =>
        {
            received = c;
            idInside = c.Id;
            Interlocked.Increment(ref calls);
        });

        Assert.False(core.Thread.ThreadState.HasFlag(ThreadState.Unstarted)); // Started by the constructor.
        Assert.True(core.Thread.Join(WaitTimeout));
        Assert.Equal(1, calls);
        Assert.Same(core, received);
        Assert.NotEqual(0, idInside);
        Assert.True(core.IsDisposed);
        Assert.True(core.IsTerminated);
        Assert.Null(core.Parent);
        Assert.Equal(0, core.Id);
        Assert.DoesNotContain(core, group.GetChildren());
        Assert.Equal(0, group.ChildCount);

        core.SendSignal(ExecutionSignal.Start); // A disposed core ignores later starts.
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ThreadCoreWithNoDisposeOnCompletionLoopsUntilTerminationIsRequested()
    {
        using var root = new ExecutionRoot();
        using var started = new ManualResetEventSlim();
        var iterations = 0;
        var exits = 0;
        using var core = new ThreadCore(
            root,
            c =>
            {
                started.Set();
                while (c.CanContinue)
                {
                    Interlocked.Increment(ref iterations);
                    c.CancellationToken.WaitHandle.WaitOne(10); // Returns as soon as termination is requested; the timeout only bounds each pass.
                }

                Interlocked.Increment(ref exits);
            },
            ExecutionCoreOptions.NoDisposeOnCompletion);

        Assert.True(started.Wait(WaitTimeout));
        Assert.False(core.IsTerminated);
        Assert.False(await core.WaitForTerminationAsync(0));
        core.RequestTermination();
        Assert.True(await core.WaitForTerminationAsync(WaitTimeout));
        Assert.True(core.Thread.Join(WaitTimeout));
        Assert.True(core.IsTerminated);
        Assert.False(core.IsDisposed);
        Assert.Same(root, core.Parent);
        Assert.Contains(core, root.GetChildren());
        Assert.Equal(1, exits);
        Assert.True(iterations > 0);
    }

    [Fact]
    public async Task ThreadCoreIgnoresCancelAndTerminateSignalsAndThenStartsOnce()
    {
        using var root = new ExecutionRoot();
        var calls = 0;
        using var core = new ThreadCore(root, _ => Interlocked.Increment(ref calls), ExecutionCoreOptions.DelayedStart);
        core.SendSignal(ExecutionSignal.Cancel);
        core.SendSignal(ExecutionSignal.Terminate);
        root.SendSignal(ExecutionSignal.Cancel);
        root.SendSignal(ExecutionSignal.Terminate);
        Assert.Equal(ThreadState.Unstarted, core.Thread.ThreadState);
        Assert.True(core.CanContinue);
        Assert.False(core.IsTerminated);
        Assert.False(core.IsDisposed);
        Assert.Equal(0, calls);

        core.SendSignal(ExecutionSignal.Start);
        Assert.True(core.Thread.Join(WaitTimeout));
        core.SendSignal(ExecutionSignal.Start);
        Assert.Equal(1, calls);
        Assert.True(core.IsDisposed);
        Assert.True(await core.WaitForTerminationAsync(WaitTimeout));
    }

    [Fact]
    public void DelayedThreadCoreCanBeMadeBackgroundBeforeStart()
    {
        using var root = new ExecutionRoot();
        var calls = 0;
        var ranOnCoreThread = false;
        var isBackground = false;
        using var core = new ThreadCore(
            root,
            c =>
            {
                ranOnCoreThread = ReferenceEquals(Thread.CurrentThread, c.Thread);
                isBackground = Thread.CurrentThread.IsBackground;
                Interlocked.Increment(ref calls);
            },
            ExecutionCoreOptions.DelayedStart);
        Assert.False(core.Thread.IsBackground); // Foreground by default.
        core.Thread.IsBackground = true;
        core.SendSignal(ExecutionSignal.Start);
        Assert.True(core.Thread.Join(WaitTimeout));
        Assert.Equal(1, calls);
        Assert.True(ranOnCoreThread);
        Assert.True(isBackground);
        Assert.True(core.IsDisposed);
    }

    [Fact]
    public async Task ThreadCoreTreatsOperationCanceledAfterTerminationAsNormalExit()
    {
        using var root = new ExecutionRoot();
        using var started = new ManualResetEventSlim();
        var reachedThrow = 0;
        var passedThrow = 0;
        var core = new ThreadCore(root, c =>
        {
            var token = c.CancellationToken;
            started.Set();
            token.WaitHandle.WaitOne(WaitTimeout);
            Volatile.Write(ref reachedThrow, 1);
            token.ThrowIfCancellationRequested(); // Would crash the test process if it escaped as an unhandled exception.
            Volatile.Write(ref passedThrow, 1);
        });

        Assert.True(started.Wait(WaitTimeout));
        core.RequestTermination();
        Assert.True(await core.WaitForTerminationAsync(WaitTimeout));
        Assert.True(core.Thread.Join(WaitTimeout));
        Assert.Equal(1, Volatile.Read(ref reachedThrow));
        Assert.Equal(0, Volatile.Read(ref passedThrow));
        Assert.True(core.IsDisposed);
        Assert.Null(core.Parent);
        Assert.DoesNotContain(core, root.GetChildren());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskCoreRunsToCompletionWhenItsDelegateObservesTermination(bool noDispose)
    {
        using var root = new ExecutionRoot();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var core = new TaskCore(
            root,
            async c =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, c.CancellationToken);
            },
            noDispose ? ExecutionCoreOptions.NoDisposeOnCompletion : ExecutionCoreOptions.None);

        await started.Task.WaitAsync(WaitTimeout);
        Assert.False(core.IsTerminated);
        core.RequestTermination();
        await core.Task.WaitAsync(WaitTimeout); // Does not throw.
        Assert.Equal(TaskStatus.RanToCompletion, core.Task.Status);
        Assert.Null(core.Task.Exception);
        Assert.True(await core.WaitForTerminationAsync(WaitTimeout));
        Assert.True(core.IsTerminated);
        Assert.Equal(!noDispose, core.IsDisposed);
    }

    [Fact]
    public async Task TaskCoreFaultsForExceptionsThatAreNotCooperativeTermination()
    {
        using var root = new ExecutionRoot();

        var notCanceled = new OperationCanceledException("Not a termination");
        using var survivor = new TaskCore(root, _ => throw notCanceled, ExecutionCoreOptions.NoDisposeOnCompletion);
        await WaitQuietlyAsync(survivor.Task);
        Assert.Equal(TaskStatus.Faulted, survivor.Task.Status);
        Assert.Same(notCanceled, survivor.Task.Exception!.InnerException);
        Assert.True(survivor.CanContinue); // Nobody requested termination.
        Assert.True(survivor.IsTerminated);
        Assert.False(survivor.IsDisposed);

        using var foreignSource = new CancellationTokenSource();
        foreignSource.Cancel();
        using var foreign = new TaskCore(root, async _ =>
        {
            await Task.Yield();
            foreignSource.Token.ThrowIfCancellationRequested(); // A canceled token that does not belong to the core.
        });
        await WaitQuietlyAsync(foreign.Task);
        Assert.Equal(TaskStatus.Faulted, foreign.Task.Status);
        Assert.IsType<OperationCanceledException>(foreign.Task.Exception!.InnerException);
        Assert.True(foreign.IsDisposed);

        var failure = new InvalidOperationException("Delegate failure");
        using var failed = new TaskCore(root, _ => throw failure);
        await WaitQuietlyAsync(failed.Task);
        Assert.Equal(TaskStatus.Faulted, failed.Task.Status);
        Assert.Same(failure, failed.Task.Exception!.InnerException);
        Assert.True(failed.IsTerminated);
        Assert.True(failed.IsDisposed);
        Assert.Null(failed.Parent);
        Assert.DoesNotContain(failed, root.GetChildren());
    }

    [Fact]
    public void CoresCreatedUnderTerminatedGroupAreDisposedInTheirConstructors()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        group.RequestTermination();
        var calls = 0;

        var task = new TaskCore(group, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        Assert.True(task.IsDisposed);
        Assert.True(task.IsTerminated);
        Assert.Null(task.Parent);
        Assert.True(task.Task.IsCompletedSuccessfully);

        var thread = new ThreadCore(group, _ => Interlocked.Increment(ref calls));
        Assert.True(thread.IsDisposed);
        Assert.True(thread.IsTerminated);
        Assert.Null(thread.Parent);
        Assert.Equal(ThreadState.Unstarted, thread.Thread.ThreadState);

        Assert.Equal(0, group.ChildCount);
        Assert.Empty(group.GetChildren());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CoresCreatedUnderTerminatedGroupWithNoDisposeOnCompletionStayAttached()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        group.RequestTermination();
        var calls = 0;

        using var task = new TaskCore(
            group,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            ExecutionCoreOptions.NoDisposeOnCompletion);
        using var thread = new ThreadCore(group, _ => Interlocked.Increment(ref calls), ExecutionCoreOptions.NoDisposeOnCompletion);
        task.SendSignal(ExecutionSignal.Start);
        thread.SendSignal(ExecutionSignal.Start);

        Assert.False(task.IsDisposed);
        Assert.True(task.IsTerminated);
        Assert.Same(group, task.Parent);
        Assert.True(task.Task.IsCompletedSuccessfully);
        Assert.False(thread.IsDisposed);
        Assert.True(thread.IsTerminated);
        Assert.Same(group, thread.Parent);
        Assert.Equal(ThreadState.Unstarted, thread.Thread.ThreadState);
        Assert.Equal(new ExecutionCore[] { task, thread }, group.GetChildren());
        Assert.True(await group.WaitForTerminationAsync(0));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DelayedCoresCanceledBeforeStartAreDisposedOnlyWhenStartArrives()
    {
        using var root = new ExecutionRoot();
        using var group = new ExecutionGroup(root);
        var calls = 0;
        using var startedTask = new TaskCore(group, Count, ExecutionCoreOptions.DelayedStart);
        using var pendingTask = new TaskCore(group, Count, ExecutionCoreOptions.DelayedStart);
        using var startedThread = new ThreadCore(group, _ => Interlocked.Increment(ref calls), ExecutionCoreOptions.DelayedStart);
        using var pendingThread = new ThreadCore(group, _ => Interlocked.Increment(ref calls), ExecutionCoreOptions.DelayedStart);
        startedTask.RequestTermination();
        startedThread.RequestTermination();
        group.RequestTermination();

        startedTask.SendSignal(ExecutionSignal.Start);
        startedThread.SendSignal(ExecutionSignal.Start);
        Assert.True(startedTask.IsDisposed);
        Assert.True(startedTask.IsTerminated);
        Assert.Null(startedTask.Parent);
        Assert.Equal(TaskStatus.Created, startedTask.Task.Status);
        Assert.True(startedThread.IsDisposed);
        Assert.True(startedThread.IsTerminated);
        Assert.Null(startedThread.Parent);
        Assert.Equal(ThreadState.Unstarted, startedThread.Thread.ThreadState);

        Assert.True(pendingTask.IsTerminated);
        Assert.False(pendingTask.IsDisposed);
        Assert.Same(group, pendingTask.Parent);
        Assert.Equal(TaskStatus.Created, pendingTask.Task.Status);
        Assert.True(pendingThread.IsTerminated);
        Assert.False(pendingThread.IsDisposed);
        Assert.Same(group, pendingThread.Parent);
        Assert.Equal(new ExecutionCore[] { pendingTask, pendingThread }, group.GetChildren());
        Assert.True(await group.WaitForTerminationAsync(0));

        group.SendSignal(ExecutionSignal.Start); // A late broadcast disposes the abandoned cores without running them.
        Assert.True(pendingTask.IsDisposed);
        Assert.True(pendingThread.IsDisposed);
        Assert.Empty(group.GetChildren());
        Assert.Equal(TaskStatus.Created, pendingTask.Task.Status);
        Assert.Equal(ThreadState.Unstarted, pendingThread.Thread.ThreadState);
        Assert.Equal(0, calls);

        Task Count(TaskCore core)
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DerivedTaskCoreIgnoresStartBeforeInitializeAndRunsOnceAfterward()
    {
        using var root = new ExecutionRoot();
        var calls = 0;
        using var core = new ManualTaskCore(root, ExecutionCoreOptions.DelayedStart);
        core.SendSignal(ExecutionSignal.Start);
        root.SendSignal(ExecutionSignal.Start);
        Assert.Throws<InvalidOperationException>(() => { _ = core.Task; });
        Assert.False(core.IsTerminated);

        var task = core.CreateTask(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        core.InitializeTask(task);
        Assert.Same(task, core.Task);
        Assert.Equal(TaskStatus.Created, task.Status); // Earlier starts are not remembered.
        Assert.Equal(0, calls);

        core.SendSignal(ExecutionSignal.Start);
        await task.WaitAsync(WaitTimeout);
        core.SendSignal(ExecutionSignal.Start);
        Assert.Equal(1, calls);
        Assert.True(core.IsTerminated);
        Assert.True(core.IsDisposed);
    }

    [Fact]
    public async Task DerivedTaskCoreWithoutDelayedStartStartsWhenInitialized()
    {
        using var root = new ExecutionRoot();
        var calls = 0;
        using var core = new ManualTaskCore(root, ExecutionCoreOptions.NoDisposeOnCompletion);
        core.SendSignal(ExecutionSignal.Start); // Ignored: there is no task yet.
        core.InitializeTask(core.CreateTask(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }));
        await core.Task.WaitAsync(WaitTimeout);
        core.SendSignal(ExecutionSignal.Start);
        Assert.Equal(1, calls);
        Assert.True(await core.WaitForTerminationAsync(WaitTimeout));
        Assert.False(core.IsDisposed);
    }

    [Fact]
    public async Task DerivedTaskCoreWithUnstartableTaskThrowsOnlyOnTheFirstStart()
    {
        using var root = new ExecutionRoot();
        using var core = new ManualTaskCore(root, ExecutionCoreOptions.DelayedStart);
        var promise = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.InitializeTask(promise.Task);
        Assert.Throws<InvalidOperationException>(() => core.SendSignal(ExecutionSignal.Start));
        core.SendSignal(ExecutionSignal.Start); // No retry of a start that cannot succeed.
        root.SendSignal(ExecutionSignal.Start);
        Assert.Equal(TaskStatus.WaitingForActivation, promise.Task.Status);
        Assert.False(core.IsTerminated);
        core.RequestTermination();
        Assert.False(core.CanContinue);
        Assert.True(core.IsTerminated); // A core whose start failed is terminated once canceled.
        Assert.True(await core.WaitForTerminationAsync(WaitTimeout));
        Assert.False(core.IsDisposed);

        promise.SetResult();
        Assert.True(core.IsTerminated);
    }

    [Fact]
    public async Task RepeatedLongRunningTaskFactoryCallsCreateIndependentTasks()
    {
        using var root = new ExecutionRoot();
        var first = 0;
        var second = 0;
        using var core = new ManualTaskCore(root, ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.NoDisposeOnCompletion);
        var firstTask = core.CreateTask(() =>
        {
            Interlocked.Increment(ref first);
            return Task.CompletedTask;
        });
        var secondTask = core.CreateTask(() =>
        {
            Interlocked.Increment(ref second);
            return Task.CompletedTask;
        });
        Assert.NotSame(firstTask, secondTask);
        Assert.Equal(TaskStatus.Created, firstTask.Status);
        Assert.Equal(TaskStatus.Created, secondTask.Status);

        core.InitializeTask(secondTask);
        core.SendSignal(ExecutionSignal.Start);
        await secondTask.WaitAsync(WaitTimeout);
        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(TaskStatus.Created, firstTask.Status);
        Assert.True(core.IsTerminated);

        firstTask.Start(); // The first task still runs its own delegate.
        await firstTask.WaitAsync(WaitTimeout);
        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.False(core.IsDisposed);
    }

    [Fact]
    public async Task GenericTaskCorePassesItselfAndDelayedConstructorStartsAfterDerivedInitialization()
    {
        using var root = new ExecutionRoot();
        SelfTaskCore? received = null;
        using var core = new SelfTaskCore(root, c =>
        {
            received = c;
            return Task.CompletedTask;
        });
        await core.Task.WaitAsync(WaitTimeout);
        Assert.Same(core, received);
        Assert.True(core.IsDisposed);

        string? observed = null;
        using var labeled = new LabeledTaskCore(
            root,
            c =>
            {
                observed = c.Label;
                return Task.CompletedTask;
            },
            "ready");
        await labeled.Task.WaitAsync(WaitTimeout);
        Assert.Equal("ready", observed);
        Assert.True(labeled.IsDisposed);
    }

    [Fact]
    public async Task TaskCoreStartAndCancellationRaceStartsAtMostOnce()
    {
        using var root = new ExecutionRoot();
        var cores = new TaskCore?[RaceIterations];
        var calls = new int[RaceIterations];
        try
        {
            RunRace(
                i => cores[i] = new TaskCore(
                    root,
                    _ =>
                    {
                        Interlocked.Increment(ref calls[i]);
                        return Task.CompletedTask;
                    },
                    ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.NoDisposeOnCompletion),
                i => cores[i]!.SendSignal(ExecutionSignal.Start),
                i => cores[i]!.RequestTermination());

            for (var i = 0; i < RaceIterations; i++)
            {
                var core = cores[i]!;
                Assert.True(await core.WaitForTerminationAsync(WaitTimeout));
                var ran = Volatile.Read(ref calls[i]);
                Assert.InRange(ran, 0, 1);
                Assert.Equal(ran == 0 ? TaskStatus.Created : TaskStatus.RanToCompletion, core.Task.Status);
                Assert.False(core.IsDisposed);
            }
        }
        finally
        {
            foreach (var core in cores)
            {
                core?.Dispose();
            }
        }
    }

    [Fact]
    public async Task ThreadCoreStartAndCancellationRaceStartsAtMostOnce()
    {
        using var root = new ExecutionRoot();
        var cores = new ThreadCore?[RaceIterations];
        var calls = new int[RaceIterations];
        try
        {
            RunRace(
                i => cores[i] = new ThreadCore(
                    root,
                    _ => Interlocked.Increment(ref calls[i]),
                    ExecutionCoreOptions.DelayedStart | ExecutionCoreOptions.NoDisposeOnCompletion),
                i => cores[i]!.SendSignal(ExecutionSignal.Start),
                i => cores[i]!.RequestTermination());

            for (var i = 0; i < RaceIterations; i++)
            {
                var core = cores[i]!;
                Assert.True(await core.WaitForTerminationAsync(WaitTimeout));
                var unstarted = core.Thread.ThreadState == ThreadState.Unstarted;
                if (!unstarted)
                {
                    Assert.True(core.Thread.Join(WaitTimeout)); // Also publishes the delegate's increment.
                }

                var ran = Volatile.Read(ref calls[i]);
                Assert.InRange(ran, 0, 1);
                Assert.Equal(unstarted ? 0 : 1, ran);
                Assert.False(core.IsDisposed);
            }
        }
        finally
        {
            foreach (var core in cores)
            {
                core?.Dispose();
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="start"/> on the calling thread and <paramref name="cancel"/> on a helper thread,
    /// released together by a barrier once per iteration after <paramref name="create"/>.
    /// </summary>
    private static void RunRace(Action<int> create, Action<int> start, Action<int> cancel)
    {
        using var barrier = new Barrier(2);
        Exception? helperException = null;
        var helper = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < RaceIterations; i++)
                {
                    if (!barrier.SignalAndWait(WaitTimeout))
                    {
                        return;
                    }

                    Thread.SpinWait(((i / 8) % 8) * 8); // Varies the interleaving.
                    cancel(i);
                    if (!barrier.SignalAndWait(WaitTimeout))
                    {
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                helperException = exception;
            }
        })
        {
            IsBackground = true,
        };

        helper.Start();
        for (var i = 0; i < RaceIterations; i++)
        {
            create(i);
            Assert.True(barrier.SignalAndWait(WaitTimeout));
            Thread.SpinWait((i % 8) * 8);
            start(i);
            Assert.True(barrier.SignalAndWait(WaitTimeout));
        }

        Assert.True(helper.Join(WaitTimeout));
        Assert.Null(helperException);
    }

    private static async Task WaitQuietlyAsync(Task task)
    {
        await task.WaitAsync(WaitTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.True(task.IsCompleted);
    }

    private sealed class ManualTaskCore : TaskCore
    {
        public ManualTaskCore(ExecutionGroup parent, ExecutionCoreOptions options)
            : base(parent, options)
        {
        }

        public Task CreateTask(Func<Task> method)
            => this.CreateLongRunningTask(this, method);

        public void InitializeTask(Task task)
            => this.Initialize(task);
    }

    private sealed class SelfTaskCore : TaskCore<SelfTaskCore>
    {
        public SelfTaskCore(ExecutionGroup parent, Func<SelfTaskCore, Task> method)
            : base(parent, method)
        {
        }
    }

    private sealed class LabeledTaskCore : TaskCore<LabeledTaskCore>
    {
        public LabeledTaskCore(ExecutionGroup parent, Func<LabeledTaskCore, Task> method, string label)
            : base(parent, method, ExecutionCoreOptions.None, true)
        {
            this.Label = label;
            this.SendSignal(ExecutionSignal.Start); // The delegate observes the fully initialized instance.
        }

        public string Label { get; }
    }
}
