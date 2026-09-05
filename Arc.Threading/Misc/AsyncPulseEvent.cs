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
                    if (current is Waiter registeredWaiter)
                    {
                        registeredWaiter.Finish(true);
                    }
                    else
                    {
                        current.TrySetResult(true);
                    }

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
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative other than -1 ms, or exceeds <see cref="int.MaxValue"/> milliseconds.</exception>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        else if (timeout != Timeout.InfiniteTimeSpan &&
            timeout.TotalMilliseconds > int.MaxValue)
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

            var needsCleanup = timeout != Timeout.InfiniteTimeSpan || cancellationToken.CanBeCanceled;
            TaskCompletionSource<bool> completion = needsCleanup
                ? new Waiter(this)
                : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref this.waiter, completion, null) != null)
            {
                continue;
            }

            if (!needsCleanup)
            {
                return completion.Task;
            }

            var tcs = (Waiter)completion;
            if (cancellationToken.CanBeCanceled)
            {
                tcs.CancellationRegistration = cancellationToken.UnsafeRegister(Waiter.Cancel, tcs);
            }

            if (timeout != Timeout.InfiniteTimeSpan)
            {
                tcs.TimeoutCts = CancellationTokenPool.Rent(); // new CancellationTokenSource(timeout);
                tcs.TimeoutCts.CancelAfter(timeout);
                tcs.TimeoutRegistration = tcs.TimeoutCts.Token.UnsafeRegister(Waiter.Cancel, tcs);
            }

            // Publish registrations before allowing the winning signal to clean them up.
            tcs.Initialize();
            return tcs.Task;
        }
    }

    private sealed class Waiter : TaskCompletionSource<bool>
    {
        private readonly AsyncPulseEvent owner;
        private int state; // 0: Initializing, 1: Ready, 2: Pulsed, 3: Canceled.

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public CancellationTokenRegistration TimeoutRegistration { get; set; }

        public CancellationTokenSource? TimeoutCts { get; set; }

        public Waiter(AsyncPulseEvent owner)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            this.owner = owner;
        }

        public static void Cancel(object? state)
        {
            var waiter = (Waiter)state!;
            if (Interlocked.CompareExchange(ref waiter.owner.waiter, null, waiter) == waiter)
            {
                waiter.Finish(false);
            }
        }

        public void Initialize()
        {
            var result = Interlocked.CompareExchange(ref this.state, 1, 0);
            if (result != 0)
            {
                this.CleanupAndComplete(result == 2);
            }
        }

        public void Finish(bool pulsed)
        {
            // Exactly one signal owns this waiter after unlinking it from the event.
            // If registration is still in progress, Initialize performs cleanup instead.
            if (Interlocked.Exchange(ref this.state, pulsed ? 2 : 3) == 1)
            {
                this.CleanupAndComplete(pulsed);
            }
        }

        private void CleanupAndComplete(bool pulsed)
        {
            this.CancellationRegistration.Dispose();
            this.TimeoutRegistration.Dispose();
            if (this.TimeoutCts is { } source)
            {
                CancellationTokenPool.TryResetAndReturn(source);
            }

            this.TrySetResult(pulsed);
        }

        // Preserved alternatives from the original cancellation and timeout state classes:
        // cancellationState.Waiter.TrySetCanceled(cancellationState.CancellationToken);
        // cancellationState.Waiter.TrySetCanceled(cancellationState.CancellationToken);
        // timeoutState.Waiter.TrySetException(new TimeoutException());
        // public CancellationToken CancellationToken { get; }
        // this.CancellationToken = cancellationToken;
        /*, CancellationToken cancellationToken*/
    }
}
