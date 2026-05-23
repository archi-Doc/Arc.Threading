// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Provides coalesced, delayed execution for an asynchronous action.
/// </summary>
/// <remarks>
/// This executor guarantees at most one pending delayed run while idle/waiting, <br/>and at most
/// one additional delayed rerun request while currently executing.
/// </remarks>
public sealed class DelayedAsyncExecutor
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
    /// Initializes a new instance of the <see cref="DelayedAsyncExecutor"/> class.
    /// </summary>
    /// <param name="action">
    /// The asynchronous action to execute after the configured delay.
    /// </param>
    /// <param name="delay">
    /// The debounce delay applied before each execution.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel pending delays and action execution.
    /// </param>
    public DelayedAsyncExecutor(Func<CancellationToken, Task> action, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        this.action = action ?? throw new ArgumentNullException(nameof(action));
        this.delay = delay;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Requests delayed execution.
    /// Requests during the waiting period are coalesced.
    /// Requests during execution schedule one additional delayed execution.
    /// </summary>
    /// <returns>
    /// true if this request changed the executor state; otherwise, false.
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
        try
        {
            while (true)
            {
                await Task.Delay(this.delay, this.cancellationToken).ConfigureAwait(false);

                // Waiting -> Running
                if (Interlocked.CompareExchange(ref this.state, Running, Waiting) != Waiting)
                {
                    return;
                }

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
        }
        finally
        {
            Volatile.Write(ref this.state, Idle);
        }
    }
}
