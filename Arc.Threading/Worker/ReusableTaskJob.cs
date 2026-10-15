// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Represents a reusable job that uses task-based asynchronous waiting.
/// </summary>
/// <remarks>
/// Completion includes aborted jobs; inspect <see cref="ReusableJob.State"/> for the outcome.
/// Canceling or timing out a wait does not cancel job processing. Finish all waits before returning the job to its pool.
/// </remarks>
public record class ReusableTaskJob : ReusableJob
{
    private TaskCompletionSource? tcs;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableTaskJob"/> class.
    /// </summary>
    public ReusableTaskJob()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableTaskJob"/> class as a copy of another job.<br/>
    /// The copy starts in the <see cref="ReusableJobState.Initial"/> state with its own completion source.
    /// </summary>
    /// <param name="original">The job to copy. Its state, options, and completion source are not copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="original"/> is <see langword="null"/>.</exception>
    protected ReusableTaskJob(ReusableTaskJob original)
        : base(original)
    {
    }

    /// <summary>
    /// Gets the <see cref="System.Threading.Tasks.Task"/> associated with this reusable job.
    /// </summary>
    /// <value>
    /// The task that completes when the job is completed or aborted. Inspect <see cref="ReusableJob.State"/> for the outcome.
    /// </value>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive: it has been returned to the pool, or it may have been rented with <see cref="ReusableJobOptions.ReturnToPoolOnCompletion"/>.</exception>
    public Task Task
    {
        get
        {
            if (this.tcs is null)
            {
                ThrowNoSynchronizationPrimitive();
            }

            return this.tcs.Task;
        }
    }

    /// <summary>
    /// Asynchronously waits until this job completes or is aborted.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the wait operation.</param>
    /// <returns>
    /// A task that completes when the job finishes, or is canceled if <paramref name="cancellationToken"/> is canceled.
    /// </returns>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive: it has been returned to the pool, or it may have been rented with <see cref="ReusableJobOptions.ReturnToPoolOnCompletion"/>.</exception>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (this.tcs is null)
        {
            ThrowNoSynchronizationPrimitive();
        }

        return this.tcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Asynchronously waits until this job completes or is aborted, with a timeout and optional cancellation.
    /// </summary>
    /// <param name="timeout">The maximum wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">A token used to cancel the wait operation.</param>
    /// <returns>
    /// A task that completes when the job finishes, is canceled if <paramref name="cancellationToken"/> is canceled,<br/>
    /// or faults with a <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses.
    /// </returns>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive: it has been returned to the pool, or it may have been rented with <see cref="ReusableJobOptions.ReturnToPoolOnCompletion"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative other than -1 ms, or exceeds 4,294,967,294 milliseconds.</exception>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.tcs is null)
        {
            ThrowNoSynchronizationPrimitive();
        }

        return this.tcs.Task.WaitAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Returns an identity-based hash code that does not change when the completion source is replaced.
    /// </summary>
    /// <returns>The hash code of the base job.</returns>
    public override int GetHashCode()
        => base.GetHashCode();

    internal override void _PrepareSynchronizationPrimitive()
    {
        if (!this.ReturnsToPoolOnCompletion)
        {// Auto-returned jobs must not be awaited, so they need no completion source.
            this.tcs ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal override void _SetSynchronizationPrimitive()
    {
        this.tcs?.TrySetResult();
    }

    internal override void _ResetSynchronizationPrimitive()
    {
        this.tcs = default;
    }

    /// <summary>
    /// Appends the members of the base job. <see cref="Task"/> is omitted because it throws for pooled jobs.
    /// </summary>
    /// <param name="builder">The builder that receives the members.</param>
    /// <returns><see langword="true"/> if members were appended; otherwise, <see langword="false"/>.</returns>
    protected override bool PrintMembers(StringBuilder builder)
        => base.PrintMembers(builder);
}
