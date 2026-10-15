// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Provides coalesced, delayed execution for an asynchronous action.
/// </summary>
/// <remarks>
/// Runs never overlap. There is at most one pending delayed run while waiting,<br/>
/// and at most one additional delayed rerun requested while the action is running. A rerun requested during a failed run is still executed.<br/>
/// The action is always started on the thread pool, never inline in <see cref="Request"/>.<br/>
/// Handle action exceptions inside the action to observe failures; <see cref="Request"/> does not expose the background task.<br/>
/// Cancellation drops pending runs, and later requests are ignored.
/// </remarks>
public sealed class DelayedTaskExecutor
{
    private const int Idle = 0;
    private const int Waiting = 1;
    private const int Running = 2;
    private const int RerunRequested = 3;

    private readonly Func<CancellationToken, Task> action;
    private readonly TimeSpan delay;
    private readonly CancellationToken cancellationToken;
    private int state;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelayedTaskExecutor"/> class.
    /// </summary>
    /// <param name="action">
    /// The asynchronous action to execute after the configured delay.
    /// </param>
    /// <param name="delay">
    /// The delay before each run, measured from the first request (or, for a rerun, from the end of the previous run).
    /// Further requests do not restart it.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels pending delays and is passed to the action for cooperative cancellation.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative or exceeds 4,294,967,294 milliseconds.</exception>
    public DelayedTaskExecutor(Func<CancellationToken, Task> action, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero || delay.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        this.action = action ?? throw new ArgumentNullException(nameof(action));
        this.delay = delay;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Requests delayed execution. This method is thread-safe and does not block.<br/>
    /// Requests during the waiting period are coalesced.<br/>
    /// Requests during execution schedule one additional delayed execution.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this request scheduled a run or a rerun; otherwise, <see langword="false"/>
    /// (the request was coalesced, or the cancellation token is canceled).
    /// </returns>
    public bool Request()
    {
        if (this.cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        while (true)
        {
            var current = Volatile.Read(ref this.state);
            switch (current)
            {
                case Idle:
                    if (Interlocked.CompareExchange(ref this.state, Waiting, Idle) == Idle)
                    {
                        _ = this.RunAsync();
                        return true;
                    }

                    break;

                case Waiting:
                    // Already scheduled. Coalesce requests during the delay.
                    return false;

                case Running:
                    // Schedule one more execution after the current one completes.
                    if (Interlocked.CompareExchange(ref this.state, RerunRequested, Running) == Running)
                    {
                        return true;
                    }

                    break;

                case RerunRequested:
                    // Already requested. Coalesce additional requests during execution.
                    return false;

                default:
                    throw new InvalidOperationException("Invalid executor state.");
            }
        }
    }

    private async Task RunAsync()
    {
        // Do not reset the state after the Running -> Idle transition.
        // The ownership of this loop is released at that point, and another loop may already be running.
        try
        {
            while (true)
            {
                // ForceYielding: a zero delay completes synchronously, which would otherwise run the action inline in Request().
                await Task.Delay(this.delay, this.cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

                // Only this loop can change the Waiting state; requests during the delay are coalesced.
                Volatile.Write(ref this.state, Running);

                await this.action(this.cancellationToken).ConfigureAwait(false);

                if (Interlocked.CompareExchange(ref this.state, Idle, Running) == Running)
                {// Running -> Idle
                    return;
                }

                // RerunRequested -> Waiting
                Volatile.Write(ref this.state, Waiting);
            }
        }
        catch (OperationCanceledException) when (this.cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref this.state, Idle);
        }
        catch
        {// Only the action can fault here, so this loop still owns the state (Running or RerunRequested).
            if (Interlocked.CompareExchange(ref this.state, Idle, Running) == RerunRequested)
            {// A failed run still honors a rerun accepted by Request() during it.
                Volatile.Write(ref this.state, Waiting);
                _ = this.RunAsync();
            }

            throw;
        }
    }
}
