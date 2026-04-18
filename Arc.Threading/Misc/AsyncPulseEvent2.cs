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
public sealed class AsyncPulseEvent2
{
    private readonly object syncObject = new();
    private TaskCompletionSource<bool>? waiter;
    private bool pulsed;

    /// <summary>
    /// Sends a pulse.<br/>
    /// If a waiter exists, it is released..<br/>
    /// Otherwise, the pulse is retained until the next wait.
    /// </summary>
    public void Pulse()
    {
        TaskCompletionSource<bool>? toRelease = null;
        lock (this.syncObject)
        {
            if (this.waiter is null)
            {
                this.pulsed = true;
            }
            else
            {
                toRelease = this.waiter;
                this.waiter = null;
            }
        }

        _ = toRelease?.TrySetResult(true);
    }

    /// <summary>
    /// Waits until a pulse is received.
    /// </summary>
    public Task Wait()
        => this.Wait(CancellationToken.None);

    /// <summary>
    /// Waits until a pulse is received, or until cancellation is requested.
    /// Only one concurrent waiter is allowed.
    /// </summary>
    public Task Wait(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        TaskCompletionSource<bool>? localWaiter = null;
        lock (this.syncObject)
        {
            if (this.pulsed)
            {
                this.pulsed = false;
                return Task.CompletedTask;
            }

            if (this.waiter is not null)
            {
                throw new InvalidOperationException("Only one waiter is allowed at a time.");
            }

            localWaiter = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            this.waiter = localWaiter;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            return localWaiter.Task;
        }

        localWaiter.Task.WaitAsync(cancellationToken);
        return this.WaitWithCancellationAsync(localWaiter, cancellationToken);
    }

    private async Task WaitWithCancellationAsync(TaskCompletionSource<bool> targetWaiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state =>
            {
                var tuple = (CancellationState)state!;
                tuple.Owner.CancelWaiter(tuple.Waiter, tuple.CancellationToken);
            },
            new CancellationState(this, targetWaiter, cancellationToken));

        await targetWaiter.Task.ConfigureAwait(false);
    }

    private void CancelWaiter(TaskCompletionSource<bool> targetWaiter, CancellationToken cancellationToken)
    {
        var shouldCancel = false;

        lock (this.syncObject)
        {
            if (ReferenceEquals(this.waiter, targetWaiter))
            {
                this.waiter = null;
                shouldCancel = true;
            }
        }

        if (shouldCancel)
        {
            _ = targetWaiter.TrySetCanceled(cancellationToken);
        }
    }

    private sealed class CancellationState
    {
        public CancellationState(
            AsyncPulseEvent2 owner,
            TaskCompletionSource<bool> waiter,
            CancellationToken cancellationToken)
        {
            this.Owner = owner;
            this.Waiter = waiter;
            this.CancellationToken = cancellationToken;
        }

        public AsyncPulseEvent2 Owner { get; }

        public TaskCompletionSource<bool> Waiter { get; }

        public CancellationToken CancellationToken { get; }
    }
}
