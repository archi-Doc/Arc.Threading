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
    public void Constructor_DefaultRetainPulseIfNoWaiter_SetsToTrue()
    {
        var ev = new AsyncPulseEvent();

        // Verify by testing behavior: pulse without waiter should be retained
        ev.Pulse();
        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Constructor_RetainPulseIfNoWaiterFalse_DoesNotRetainPulse()
    {
        var ev = new AsyncPulseEvent(retainPulseIfNoWaiter: false);

        // Pulse with no waiter should be dropped
        ev.Pulse();
        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        // Now pulse again to complete it
        ev.Pulse();
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Pulse_NoWaiter_RetainsPulseWhenRetainTrue()
    {
        var ev = new AsyncPulseEvent(retainPulseIfNoWaiter: true);

        ev.Pulse();

        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(task.IsCompletedSuccessfully);
        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public void Pulse_NoWaiter_DropsPulseWhenRetainFalse()
    {
        var ev = new AsyncPulseEvent(retainPulseIfNoWaiter: false);

        ev.Pulse();

        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);
    }

    [Fact]
    public void Pulse_AlreadyPulsed_DoesNothing()
    {
        var ev = new AsyncPulseEvent(retainPulseIfNoWaiter: true);

        ev.Pulse();
        ev.Pulse(); // Second pulse should be no-op when already pulsed
        ev.Pulse(); // Third pulse should also be no-op

        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Pulse_WithWaiter_ReleasesWaiter()
    {
        var ev = new AsyncPulseEvent();

        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        ev.Pulse();

        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task Pulse_WithWaiter_SetsResultToTrue()
    {
        var ev = new AsyncPulseEvent();

        var waitTask = ev.WaitAsync(TestContext.Current.CancellationToken);
        ev.Pulse();

        var result = await waitTask;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitAsync_NoParameters_DelegatesToOverloadWithInfiniteTimeout()
    {
        var ev = new AsyncPulseEvent();

        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        ev.Pulse();
        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitAsync_MillisecondsTimeout_DelegatesToTimeSpanOverload()
    {
        var ev = new AsyncPulseEvent();

        var task = ev.WaitAsync(5000, TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        ev.Pulse();
        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitAsync_AlreadyCanceledToken_ReturnsFalse()
    {
        var ev = new AsyncPulseEvent();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = ev.WaitAsync(cts.Token);
        Assert.True(task.IsCompletedSuccessfully);

        var result = await task;
        Assert.False(result);
    }

    [Fact]
    public async Task WaitAsync_AfterPulse_ReturnsTrueImmediately()
    {
        var ev = new AsyncPulseEvent();

        ev.Pulse();
        var task = ev.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitAsync_MultipleWaiters_ThrowsInvalidOperationException()
    {
        var ev = new AsyncPulseEvent();

        var first = ev.WaitAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => ev.WaitAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task WaitAsync_NoCancellationToken_WaitsForPulse()
    {
        var ev = new AsyncPulseEvent();

        var task = ev.WaitAsync(CancellationToken.None);
        Assert.False(task.IsCompleted);

        ev.Pulse();
        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitAsync_WithCancellableToken_CanBeCanceled()
    {
        var ev = new AsyncPulseEvent();

        using var cts = new CancellationTokenSource();
        var task = ev.WaitAsync(cts.Token);

        Assert.False(task.IsCompleted);
        cts.Cancel();

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(task.IsCompletedSuccessfully);

        var result = await task;
        Assert.False(result);
    }

    [Fact]
    public async Task WaitAsync_WithTimeout_TimesOut()
    {
        var ev = new AsyncPulseEvent();

        var task = ev.WaitAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        var result = await task;
        Assert.False(result);
    }

    [Fact]
    public async Task WaitAsync_WithTimeoutMilliseconds_TimesOut()
    {
        var ev = new AsyncPulseEvent();

        var task = ev.WaitAsync(50, TestContext.Current.CancellationToken);

        var result = await task;
        Assert.False(result);
    }

    [Fact]
    public async Task WaitAsync_WithTimeoutAndCancellation_CancelsFirst()
    {
        var ev = new AsyncPulseEvent();

        using var cts = new CancellationTokenSource();
        var task = ev.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        cts.Cancel();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var result = await task;
        Assert.False(result);
    }

    [Fact]
    public async Task WaitAsync_WithTimeoutAndCancellation_PulsedBeforeTimeout()
    {
        var ev = new AsyncPulseEvent();

        using var cts = new CancellationTokenSource();
        var task = ev.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        ev.Pulse();

        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task WaitAsync_AfterCancellation_NextWaitWorks()
    {
        var ev = new AsyncPulseEvent();

        using (var cts = new CancellationTokenSource())
        {
            var canceledTask = ev.WaitAsync(cts.Token);
            cts.Cancel();

            await Task.Delay(50, TestContext.Current.CancellationToken);
            var result = await canceledTask;
            Assert.False(result);
        }

        var next = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted);

        ev.Pulse();
        var nextResult = await next;
        Assert.True(nextResult);
    }

    [Fact]
    public async Task Pulse_AfterCanceledWait_IsRetained()
    {
        var ev = new AsyncPulseEvent();

        using (var cts = new CancellationTokenSource())
        {
            var task = ev.WaitAsync(cts.Token);
            cts.Cancel();

            await Task.Delay(50, TestContext.Current.CancellationToken);
            var result = await task;
            Assert.False(result);
        }

        ev.Pulse();

        var next = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(next.IsCompletedSuccessfully);
        var nextResult = await next;
        Assert.True(nextResult);
    }

    [Fact]
    public async Task WaitAsync_RepeatedUsage_WorksCorrectly()
    {
        var ev = new AsyncPulseEvent();

        for (var i = 0; i < 10; i++)
        {
            var waitTask = ev.WaitAsync(TestContext.Current.CancellationToken);
            ev.Pulse();
            var result = await waitTask;
            Assert.True(result);
        }
    }

    [Fact]
    public async Task WaitAsync_ConcurrentPulses_OnlyOneWaiterReleased()
    {
        var ev = new AsyncPulseEvent();

        var first = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);

        ev.Pulse();
        var result1 = await first;
        Assert.True(result1);

        var second = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        ev.Pulse();
        var result2 = await second;
        Assert.True(result2);
    }

    [Fact]
    public async Task WaitAsync_WithTimeoutNoCancellation_TimesOut()
    {
        var ev = new AsyncPulseEvent();

        var task = ev.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        var result = await task;
        Assert.False(result);
    }

    [Fact]
    public async Task MultiplePulses_BeforeWait_AreCoalesced()
    {
        var ev = new AsyncPulseEvent();

        ev.Pulse();
        ev.Pulse();
        ev.Pulse();

        var first = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(first.IsCompletedSuccessfully);
        var result1 = await first;
        Assert.True(result1);

        var second = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        ev.Pulse();
        var result2 = await second;
        Assert.True(result2);
    }

    [Fact]
    public async Task Pulse_MultipleTimes_WithDropMode_DoesNotRetain()
    {
        var ev = new AsyncPulseEvent(retainPulseIfNoWaiter: false);

        // Multiple pulses with no waiter should all be dropped
        ev.Pulse();
        ev.Pulse();
        ev.Pulse();

        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        ev.Pulse();
        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public void Constructor_WithTrue_RetainsPulse()
    {
        var ev = new AsyncPulseEvent(true);
        ev.Pulse();
        var task = ev.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(task.IsCompletedSuccessfully);
    }
}
