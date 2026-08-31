// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents a reusable worker job that supports synchronous waiting for completion.
/// </summary>
/// <remarks>
/// This job type allocates a <see cref="ManualResetEventSlim"/> when the job is rented from the worker.<br/>
/// Call <see cref="Wait(CancellationToken)"/> or <see cref="Wait(TimeSpan, CancellationToken)"/> to block until completion.<br/>
/// If asynchronous waiting is acceptable, <see cref="ReusableTaskJob"/> is recommended.
/// </remarks>
public record class ReusableThreadJob : ReusableJob
{
    private ManualResetEventSlim? eventSlim;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableThreadJob"/> class.
    /// </summary>
    public ReusableThreadJob()
    {
    }

    /// <summary>
    /// Blocks the calling thread until this job is signaled as completed.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the wait operation.
    /// </param>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive (it has been returned to the pool).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public void Wait(CancellationToken cancellationToken = default)
    {
        if (this.eventSlim is null)
        {
            ThrowNoSynchronizationPrimitive();
        }

        this.eventSlim.Wait(cancellationToken);
    }

    /// <summary>
    /// Blocks the calling thread until this job is signaled as completed, a timeout expires, or cancellation is requested.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the job to complete.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the wait operation.</param>
    /// <returns>
    /// <see langword="true"/> if the job was signaled as completed before the timeout elapsed; otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive (it has been returned to the pool).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public bool Wait(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.eventSlim is null)
        {
            ThrowNoSynchronizationPrimitive();
        }

        return this.eventSlim.Wait(timeout, cancellationToken);
    }

    internal override void _PrepareSynchronizationPrimitive()
    {
        this.eventSlim ??= new(false);
    }

    internal override void _SetSynchronizationPrimitive()
    {
        this.eventSlim?.Set();
    }

    internal override void _ResetSynchronizationPrimitive()
    {
        this.eventSlim = default;
    }
}
