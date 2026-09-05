// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using Arc.Threading;

#pragma warning disable xUnit1051 // These tests intentionally exercise default tokens and use bounded waits.

public class WorkTests
{
    [Fact]
    public async Task SingleTaskRejectsOverlapAndRecoversAfterFailure()
    {
        var single = new SingleTask();
        Assert.Throws<ArgumentNullException>(() => { _ = single.TryRun((Action)null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = single.TryRun((Func<Task>)null!); });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = single.TryRun(() => gate.Task)!;
        Assert.Same(first, single.RunningTask);
        Assert.Null(single.TryRun(() => { }));
        Assert.Null(single.TryRun(() => Task.CompletedTask));
        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(single.RunningTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => single.TryRun((Action)(() => throw new InvalidOperationException()))!);
        await single.TryRun(() => { })!;
    }

    [Fact]
    public async Task UniqueWorkJoinsAsyncWorkAndPreservesOriginalException()
    {
        Assert.Throws<ArgumentNullException>(() => new UniqueWork((Action)null!));
        Assert.Throws<ArgumentNullException>(() => new UniqueWork((Func<Task>)null!));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var work = new UniqueWork(async () =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await gate.Task;
        });
        var first = work.Run();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joined = new Task[100];
        Parallel.For(0, joined.Length, i => joined[i] = work.Run());
        Assert.All(joined, task => Assert.Same(first, task));
        gate.SetResult();
        await first;
        Assert.Equal(1, calls);
        await work.Run();
        Assert.Equal(2, calls);

        var failing = new UniqueWork((Func<Task>)(() => Task.FromException(new InvalidOperationException("original"))));
        Assert.Equal("original", (await Assert.ThrowsAsync<InvalidOperationException>(failing.Run)).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(failing.Run);
    }

    [Fact]
    public async Task UniqueWorkFastCompletionCanBeContendedRepeatedly()
    {
        var active = 0;
        var overlap = 0;
        var calls = 0;
        var work = new UniqueWork(() =>
        {
            if (Interlocked.Increment(ref active) != 1)
            {
                Interlocked.Increment(ref overlap);
            }

            Interlocked.Increment(ref calls);
            Interlocked.Decrement(ref active);
        });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var n = 0; n < 200; n++)
            {
                await work.Run();
            }
        }))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(calls > 0);
        Assert.Equal(0, overlap);
    }

    [Fact]
    public async Task DelayedExecutorCoalescesWaitingAndSchedulesOneRerun()
    {
        using var source = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rerun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var executor = new DelayedTaskExecutor(
            async token =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult();
                    await release.Task.WaitAsync(token);
                }
                else
                {
                    rerun.TrySetResult();
                }
            },
            TimeSpan.FromMilliseconds(30),
            source.Token);
        Assert.True(executor.Request());
        Assert.False(executor.Request());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(executor.Request());
        Assert.False(executor.Request());
        release.SetResult();
        await rerun.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        source.Cancel();
        Assert.False(executor.Request());
    }

    [Fact]
    public void DelayedExecutorValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new DelayedTaskExecutor(null!, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelayedTaskExecutor(_ => Task.CompletedTask, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelayedTaskExecutor(_ => Task.CompletedTask, TimeSpan.MaxValue));
    }
}
