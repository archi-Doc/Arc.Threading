// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents a reusable worker job that supports synchronous waiting for completion.
/// </summary>
/// <remarks>
/// This job type reuses one <see cref="ManualResetEventSlim"/> across rentals.<br/>
/// All waiters must finish before the job is returned to the pool.<br/>
/// Completion includes aborted jobs; inspect <see cref="ReusableJob.State"/> for the outcome.<br/>
/// Canceling or timing out a wait does not cancel job processing.<br/>
/// Call <see cref="Wait(CancellationToken)"/> or <see cref="Wait(TimeSpan, CancellationToken)"/> to block until completion.<br/>
/// If asynchronous waiting is acceptable, <see cref="ReusableTaskJob"/> is recommended.
/// </remarks>
public record class ReusableBlockingJob : ReusableJob
{
    private ManualResetEventSlim? eventSlim;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableBlockingJob"/> class.
    /// </summary>
    public ReusableBlockingJob()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableBlockingJob"/> class as a copy of another job.<br/>
    /// The copy starts in the <see cref="ReusableJobState.Initial"/> state with its own wait handle.
    /// </summary>
    /// <param name="original">The job to copy. Its state, options, and wait handle are not copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="original"/> is <see langword="null"/>.</exception>
    protected ReusableBlockingJob(ReusableBlockingJob original)
        : base(original)
    {
    }

    /// <summary>
    /// Blocks the calling thread until this job completes or is aborted.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the wait operation.
    /// </param>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive (it has been returned to the pool).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public void Wait(CancellationToken cancellationToken = default)
    {
        if (this.eventSlim is null || this.State == ReusableJobState.Pooled)
        {
            ThrowNoSynchronizationPrimitive();
        }

        this.eventSlim.Wait(cancellationToken);
    }

    /// <summary>
    /// Blocks the calling thread until this job completes or is aborted, a timeout expires, or cancellation is requested.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the job to complete, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the wait operation.</param>
    /// <returns>
    /// <see langword="true"/> if the job completed or was aborted before the timeout elapsed; otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive (it has been returned to the pool).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative other than -1 ms, or exceeds <see cref="int.MaxValue"/> milliseconds.</exception>
    public bool Wait(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.eventSlim is null || this.State == ReusableJobState.Pooled)
        {
            ThrowNoSynchronizationPrimitive();
        }

        return this.eventSlim.Wait(timeout, cancellationToken);
    }

    /// <summary>
    /// Returns an identity-based hash code.
    /// </summary>
    /// <returns>The hash code of the base job.</returns>
    public override int GetHashCode()
        => base.GetHashCode();

    internal override void _PrepareSynchronizationPrimitive()
    {
        if (this.eventSlim is null)
        {
            this.eventSlim = new(false);
        }
        else
        {
            this.eventSlim.Reset();
        }
    }

    internal override void _SetSynchronizationPrimitive()
    {
        this.eventSlim?.Set();
    }
}
