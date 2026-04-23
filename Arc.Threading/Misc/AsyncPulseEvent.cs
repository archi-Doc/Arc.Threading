// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// An async pulse event with a single waiter.
/// By default, a pulse is retained until it is consumed by one wait operation.
/// If multiple pulses arrive before a wait, they are coalesced into one.
/// </summary>
public sealed class AsyncPulseEvent
{
    private static readonly TaskCompletionSource<bool> PulsedSentinel;

    private readonly bool retainPulseIfNoWaiter;
    private TaskCompletionSource<bool>? waiter; // null: No waiter, PulsedSentinel: Pulsed, Other: Waiting

    static AsyncPulseEvent()
    {
        PulsedSentinel = new(); // Completed sentinel that represents a retained pulse.
        PulsedSentinel.SetResult(true);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncPulseEvent"/> class.
    /// </summary>
    /// <param name="retainPulseIfNoWaiter">
    /// <see langword="true"/> to retain a pulse when no waiter exists, so the next <see cref="WaitAsync(CancellationToken)"/> consumes it.
    /// <see langword="false"/> to drop the pulse when no waiter exists at the time of <see cref="Pulse"/>.
    /// </param>
    public AsyncPulseEvent(bool retainPulseIfNoWaiter = true)
    {
        this.retainPulseIfNoWaiter = retainPulseIfNoWaiter;
    }

    /// <summary>
    /// Sends a pulse.<br/>
    /// If a waiter exists, it is released.<br/>
    /// Otherwise, the pulse is either retained or dropped depending on <c>retainPulseIfNoWaiter</c>.
    /// </summary>
    public void Pulse()
    {
        while (true)
        {
            var current = Volatile.Read(ref this.waiter);
            if (current is null)
            {// No waiter
                if (!this.retainPulseIfNoWaiter)
                {// Drop the pulse.
                    return;
                }

                if (Interlocked.CompareExchange(ref this.waiter, PulsedSentinel, null) is null)
                {// Retain the pulse.
                    return;
                }
            }
            else if (ReferenceEquals(current, PulsedSentinel))
            {// Already pulsed
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
    /// Waits asynchronously for a pulse, with optional cancellation.
    /// </summary>
    /// <param name="cancellationToken"> A token used to observe cancellation while waiting.</param>
    /// <returns>
    /// A task that resolves to <see langword="true"/> when a pulse is consumed; otherwise
    /// <see langword="false"/> if the wait is canceled or times out.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when another wait operation is already in progress. Only one concurrent waiter is supported.
    /// </exception>
    public Task<bool> WaitAsync(CancellationToken cancellationToken = default)
        => this.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// Waits asynchronously for a pulse, with a timeout specified in milliseconds and optional cancellation.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait for a pulse.</param>
    /// <param name="cancellationToken"> A token used to observe cancellation while waiting.</param>
    /// <returns>
    /// A task that resolves to <see langword="true"/> when a pulse is consumed; otherwise
    /// <see langword="false"/> if the wait is canceled or times out.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when another wait operation is already in progress. Only one concurrent waiter is supported.
    /// </exception>
    public Task<bool> WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite);
        return this.WaitAsync(TimeSpan.FromMilliseconds(millisecondsTimeout), cancellationToken);
    }

    /// <summary>
    /// Waits asynchronously for a pulse, with optional timeout and cancellation.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for a pulse. Use <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">A token used to observe cancellation while waiting.</param>
    /// <returns>
    /// A task that resolves to <see langword="true"/> when a pulse is consumed; otherwise <see langword="false"/> if the wait is canceled or times out.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when another wait operation is already in progress. Only one concurrent waiter is supported.
    /// </exception>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(false); // Task.FromCanceled(cancellationToken);
        }

        while (true)
        {
            var current = Volatile.Read(ref this.waiter);
            if (ReferenceEquals(current, PulsedSentinel))
            {// Pulsed
                if (Interlocked.CompareExchange(ref this.waiter, null, PulsedSentinel) == PulsedSentinel)
                {
                    return Task.FromResult(true); // return Task.CompletedTask;
                }

                continue;
            }

            if (current is not null)
            {
                throw new InvalidOperationException("Only one waiter is allowed at a time.");
            }

            if (timeout == TimeSpan.Zero)
            {
                return Task.FromResult(false);
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
                            // cancellationState.Waiter.TrySetCanceled(cancellationState.CancellationToken);
                            cancellationState.Waiter.TrySetResult(false);
                        }
                    },
                    new CancellationState(this, tcs));

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
                                // cancellationState.Waiter.TrySetCanceled(cancellationState.CancellationToken);
                                cancellationState.Waiter.TrySetResult(false);
                            }
                        },
                        new CancellationState(this, tcs));
                }

                var timeoutCts = CancellationTokenHelper.Pool.Rent(); // new CancellationTokenSource(timeout);
                timeoutCts.CancelAfter(timeout);
                var timeoutRegistration = timeoutCts.Token.UnsafeRegister(
                    static state =>
                    {
                        var timeoutState = (TimeoutState)state!;
                        if (Interlocked.CompareExchange(ref timeoutState.Owner.waiter, null, timeoutState.Waiter) == timeoutState.Waiter)
                        {
                            // timeoutState.Waiter.TrySetException(new TimeoutException());
                            timeoutState.Waiter.TrySetResult(false);
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
                            CancellationTokenHelper.Pool.Return(cleanupState.TimeoutCts);
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

        // public CancellationToken CancellationToken { get; }

        public CancellationState(AsyncPulseEvent owner, TaskCompletionSource<bool> waiter/*, CancellationToken cancellationToken*/)
        {
            this.Owner = owner;
            this.Waiter = waiter;
            // this.CancellationToken = cancellationToken;
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
