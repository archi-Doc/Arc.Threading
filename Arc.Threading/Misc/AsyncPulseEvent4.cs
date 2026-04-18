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
public sealed class AsyncPulseEvent4
{
    private static readonly TaskCompletionSource<bool> PulsedSentinel;

    private TaskCompletionSource<bool>? waiter; // null:No waiter, PulsedSentinel:Pulsed, Other:Waiting

    static AsyncPulseEvent4()
    {
        PulsedSentinel = new(); // Completed sentinel that represents a retained pulse.
        PulsedSentinel.SetResult(true);
    }

    /// <summary>
    /// Sends a pulse.<br/>
    /// If a waiter exists, it is released.<br/>
    /// Otherwise, the pulse is retained until the next wait.
    /// </summary>
    public void Pulse()
    {
        while (true)
        {
            var current = Volatile.Read(ref this.waiter);
            if (current is null)
            {// No waiter -> Pulsed
                if (Interlocked.CompareExchange(ref this.waiter, PulsedSentinel, null) is null)
                {
                    return;
                }
            }
            else if (ReferenceEquals(current, PulsedSentinel))
            {// Pulsed
                return;
            }
            else
            {// Waiting -> Pulse
                if (Interlocked.CompareExchange(ref this.waiter, null, current) == current)
                {
                    current.TrySetResult(true);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Waits until a pulse is received.
    /// </summary>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public Task WaitAsync()
        => this.WaitAsync(CancellationToken.None);

    /// <summary>
    /// Waits until a pulse is received, or until cancellation is requested.
    /// Only one concurrent waiter is allowed.
    /// </summary>
    /// <param name="cancellationToken">The CancellationToken to monitor for a cancellation request.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        while (true)
        {
            var current = Volatile.Read(ref this.waiter);
            if (ReferenceEquals(current, PulsedSentinel))
            {// Pulsed
                if (Interlocked.CompareExchange(ref this.waiter, null, PulsedSentinel) == PulsedSentinel)
                {
                    return Task.CompletedTask;
                }

                continue;
            }

            if (current is not null)
            {
                throw new InvalidOperationException("Only one waiter is allowed at a time.");
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref this.waiter, tcs, null) != null)
            {
                continue;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                return tcs.Task;
            }

            var registration = cancellationToken.Register(
                static state =>
                {
                    var s = (CancellationState)state!;
                    if (Interlocked.CompareExchange(ref s.Owner.waiter, null, s.Waiter) == s.Waiter)
                    {
                        s.Waiter.TrySetCanceled(s.Token);
                    }
                },
                new CancellationState(this, tcs, cancellationToken));

            _ = tcs.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return tcs.Task;
        }
    }

    private sealed record class CancellationState(AsyncPulseEvent4 Owner, TaskCompletionSource<bool> Waiter, CancellationToken Token);
}
