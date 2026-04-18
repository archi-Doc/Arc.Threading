// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// An async pulse event with a single waiter.
/// A pulse is retained until it is consumed by one wait operation.
/// If multiple pulses arrive before a wait, they are coalesced into one.
/// </summary>
public sealed class AsyncPulseEvent3
{
    private static readonly TaskCompletionSource<bool> PulsedSentinel;

    private TaskCompletionSource<bool>? waiter;

    static AsyncPulseEvent3()
    {
        PulsedSentinel = new();
        PulsedSentinel.SetResult(true);
    }

    /// <summary>
    /// Sends a pulse.<br/>
    /// If a waiter exists, it is released..<br/>
    /// Otherwise, the pulse is retained until the next wait.
    /// </summary>
    public void Pulse()
    {
        var prev = Interlocked.Exchange(ref this.waiter, PulsedSentinel);
        if (prev is not null && prev != PulsedSentinel)
        {
            prev.TrySetResult(true);
        }
    }

    /// <summary>
    /// Waits until a pulse is received.
    /// </summary>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public Task Wait()
        => this.Wait(CancellationToken.None);

    /// <summary>
    /// Waits until a pulse is received, or until cancellation is requested.
    /// Only one concurrent waiter is allowed.
    /// </summary>
    /// <param name="cancellationToken">The CancellationToken to monitor for a cancellation request.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public Task Wait(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (Interlocked.CompareExchange(ref this.waiter, null, PulsedSentinel) == PulsedSentinel)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prev = Interlocked.CompareExchange(ref this.waiter, tcs, null);
        if (prev == PulsedSentinel)
        {
            Interlocked.CompareExchange(ref this.waiter, null, PulsedSentinel);
            return Task.CompletedTask;
        }

        if (prev is not null)
        {
            throw new InvalidOperationException("Only one waiter is allowed at a time.");
        }

        var registration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref this.waiter, null, tcs) == tcs)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
        });

        tcs.Task.ContinueWith(
            static (_, r) => ((CancellationTokenRegistration)r!).Dispose(),
            registration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return tcs.Task;
    }
}
