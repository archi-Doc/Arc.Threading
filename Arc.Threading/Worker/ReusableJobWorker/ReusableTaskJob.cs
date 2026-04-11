// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Represents a reusable job that uses task-based asynchronous waiting.
/// </summary>
public record class ReusableTaskJob : ReusableJobBase
{
    private readonly AsyncPulseEvent pulseEvent;

    public ReusableTaskJob()
    {
        this.pulseEvent = new();
    }

    /// <summary>
    /// Asynchronously waits until this job is completed or the specified timeout interval elapses.
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous wait operation.
    /// </returns>
    public Task Wait(CancellationToken cancellationToken = default)
    {
        return this.pulseEvent.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Asynchronously waits until this job is completed or the specified timeout interval elapses.
    /// </summary>
    /// <param name="timeout">
    /// The maximum amount of time to wait for a signal before the wait operation completes.
    /// </param>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous wait operation.
    /// </returns>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return this.pulseEvent.WaitAsync(timeout, cancellationToken);
    }

    internal override void SetInternal()
    {
        this.pulseEvent.Pulse();
    }

    internal override void ResetInternal()
    {
    }
}
