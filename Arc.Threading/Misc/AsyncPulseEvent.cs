// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Collections;

namespace Arc.Threading;

/// <summary>
/// An async pulse event with a single waiter.
/// A pulse is retained until it is consumed by one wait operation.
/// If multiple pulses arrive before a wait, they are coalesced into one.
/// </summary>
public sealed class AsyncPulseEvent
{
    private const int CtsPoolCapacity = 32;
    private static readonly TaskCompletionSource<bool> PulsedSentinel;
    private static readonly ObjectPool<CancellationTokenSource> CtsPool = new(() => new CancellationTokenSource(), CtsPoolCapacity);

    private TaskCompletionSource<bool>? waiter; // null:No waiter, PulsedSentinel:Pulsed, Other:Waiting

    static AsyncPulseEvent()
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
            {// Waiting -> Pulse -> No Waiter
                if (Interlocked.CompareExchange(ref this.waiter, null, current) == current)
                {
                    current.TrySetResult(true);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Waits until a pulse is received, or until cancellation is requested.<br/>
    /// Only one concurrent waiter is allowed.
    /// </summary>
    /// <param name="cancellationToken">The CancellationToken to monitor for a cancellation request.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public Task WaitAsync(CancellationToken cancellationToken = default)
        => this.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
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

            if (timeout == Timeout.InfiniteTimeSpan)
            {// No timeout
                if (!cancellationToken.CanBeCanceled)
                {
                    return tcs.Task;
                }

                var registration = cancellationToken.UnsafeRegister(
                    static state =>
                    {
                        var cancellationState = (CancellationState)state!;
                        if (Interlocked.CompareExchange(ref cancellationState.Owner.waiter, null, cancellationState.Waiter) == cancellationState.Waiter)
                        {
                            cancellationState.Waiter.TrySetCanceled(cancellationState.CancellationToken);
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
            else
            {// CancellationToken + Timeout
                CancellationTokenRegistration cancellationRegistration = default;
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationRegistration = cancellationToken.UnsafeRegister(
                        static state =>
                        {
                            var cancellationState = (CancellationState)state!;
                            if (Interlocked.CompareExchange(ref cancellationState.Owner.waiter, null, cancellationState.Waiter) == cancellationState.Waiter)
                            {
                                cancellationState.Waiter.TrySetCanceled(cancellationState.CancellationToken);
                            }
                        },
                        new CancellationState(this, tcs, cancellationToken));
                }

                var timeoutCts = CtsPool.Rent(); // new CancellationTokenSource(timeout);
                timeoutCts.CancelAfter(timeout);
                var timeoutRegistration = timeoutCts.Token.UnsafeRegister(
                    static state =>
                    {
                        var timeoutState = (TimeoutState)state!;
                        if (Interlocked.CompareExchange(ref timeoutState.Owner.waiter, null, timeoutState.Waiter) == timeoutState.Waiter)
                        {
                            timeoutState.Waiter.TrySetException(new TimeoutException());
                        }
                    },
                    new TimeoutState(this, tcs));

                _ = tcs.Task.ContinueWith(
                    static (_, state) =>
                    {
                        var cleanupState = (CleanupState)state!;
                        cleanupState.CancellationRegistration.Dispose();
                        cleanupState.TimeoutRegistration.Dispose();
                        if (cleanupState.TimeoutCts.TryReset())
                        {
                            CtsPool.Return(cleanupState.TimeoutCts);
                        }
                        else
                        {
                            cleanupState.TimeoutCts.Dispose();
                        }
                    },
                    new CleanupState(cancellationRegistration, timeoutRegistration, timeoutCts),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                return tcs.Task;
            }
        }
    }

    private sealed class CancellationState
    {
        public AsyncPulseEvent Owner { get; }

        public TaskCompletionSource<bool> Waiter { get; }

        public CancellationToken CancellationToken { get; }

        public CancellationState(AsyncPulseEvent owner, TaskCompletionSource<bool> waiter, CancellationToken cancellationToken)
        {
            this.Owner = owner;
            this.Waiter = waiter;
            this.CancellationToken = cancellationToken;
        }
    }

    private sealed class TimeoutState
    {
        public AsyncPulseEvent Owner { get; }

        public TaskCompletionSource<bool> Waiter { get; }

        public TimeoutState(AsyncPulseEvent owner, TaskCompletionSource<bool> waiter)
        {
            this.Owner = owner;
            this.Waiter = waiter;
        }
    }

    private sealed class CleanupState
    {
        public CancellationTokenRegistration CancellationRegistration { get; }

        public CancellationTokenRegistration TimeoutRegistration { get; }

        public CancellationTokenSource TimeoutCts { get; }

        public CleanupState(CancellationTokenRegistration cancellationRegistration, CancellationTokenRegistration timeoutRegistration, CancellationTokenSource timeoutCts)
        {
            this.CancellationRegistration = cancellationRegistration;
            this.TimeoutRegistration = timeoutRegistration;
            this.TimeoutCts = timeoutCts;
        }
    }
}
