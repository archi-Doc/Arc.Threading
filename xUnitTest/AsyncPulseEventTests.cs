// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace xUnitTest;

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Threading;
using Xunit;

public sealed class AsyncPulseEventTests
{
    [Fact]
    public async Task WaitAsync_AfterPulse_CompletesImmediately()
    {
        var ev = new AsyncPulseEvent4();

        ev.Pulse();
        var task = ev.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task WaitAsync_BeforePulse_CompletesAfterPulse()
    {
        var ev = new AsyncPulseEvent4();

        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        ev.Pulse();

        await task;
    }

    [Fact]
    public async Task MultiplePulses_BeforeWait_AreCoalescedIntoOne()
    {
        var ev = new AsyncPulseEvent4();

        ev.Pulse();
        ev.Pulse();
        ev.Pulse();

        var first = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(first.IsCompletedSuccessfully);
        await first;

        var second = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        ev.Pulse();
        await second;
    }

    [Fact]
    public async Task Pulse_ReleasesOnlyOneWait()
    {
        var ev = new AsyncPulseEvent4();

        var first = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);

        ev.Pulse();
        await first;

        var second = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        ev.Pulse();
        await second;
    }

    [Fact]
    public async Task WaitAsync_WithAlreadyCanceledToken_ReturnsCanceledTask()
    {
        var ev = new AsyncPulseEvent4();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = ev.WaitAsync(cts.Token);

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
    }

    [Fact]
    public async Task WaitAsync_CanBeCanceledAfterStart()
    {
        var ev = new AsyncPulseEvent4();

        using var cts = new CancellationTokenSource();
        var task = ev.WaitAsync(cts.Token);

        Assert.False(task.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
    }

    [Fact]
    public async Task WaitAsync_CanceledWait_DoesNotBreakNextWait()
    {
        var ev = new AsyncPulseEvent4();

        using (var cts = new CancellationTokenSource())
        {
            var canceledTask = ev.WaitAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await canceledTask);
        }

        var next = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted);

        ev.Pulse();

        await next;
    }

    [Fact]
    public async Task Pulse_AfterCanceledWait_IsRetained()
    {
        var ev = new AsyncPulseEvent4();

        using (var cts = new CancellationTokenSource())
        {
            var task = ev.WaitAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
        }

        ev.Pulse();

        var next = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(next.IsCompletedSuccessfully);
        await next;
    }

    [Fact]
    public async Task Pulse_CanBeCalledFromMultipleThreads()
    {
        var ev = new AsyncPulseEvent4();

        for (var i = 0; i < 100; i++)
        {
            var waitTask = ev.WaitAsync(TestContext.Current.CancellationToken);

            await Task.Run(() => ev.Pulse(), TestContext.Current.CancellationToken);

            await waitTask;
        }
    }

    [Fact]
    public async Task WaitAsync_CanBeUsedRepeatedly()
    {
        var ev = new AsyncPulseEvent4();

        for (var i = 0; i < 1000; i++)
        {
            var waitTask = ev.WaitAsync(TestContext.Current.CancellationToken);
            ev.Pulse();
            await waitTask;
        }
    }

    [Fact]
    public async Task WaitAsync_Cancel()
    {
        var cts = new CancellationTokenSource();
        var ev = new AsyncPulseEvent4();

        var task = ev.WaitAsync(cts.Token);
        Assert.False(task.IsCompleted);
        cts.Cancel();

        ev.Pulse();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
    }
}
