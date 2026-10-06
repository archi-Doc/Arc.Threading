// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
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
    /// Gets the <see cref="System.Threading.Tasks.Task"/> associated with this reusable job.
    /// </summary>
    /// <value>
    /// The task that completes when the job is completed or aborted. Inspect <see cref="ReusableJob.State"/> for the outcome.
    /// </value>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive (it has been returned to the pool).</exception>
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
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive (it has been returned to the pool).</exception>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (this.tcs is null)
        {
            ThrowNoSynchronizationPrimitive();
        }

        return this.tcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Starts an asynchronous wait for completion with a timeout and optional cancellation.
    /// </summary>
    /// <param name="timeout">The maximum wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">A token used to cancel the wait operation.</param>
    /// <returns>
    /// A task that completes when the job finishes, is canceled if <paramref name="cancellationToken"/> is canceled,<br/>
    /// or faults with a <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses.
    /// </returns>
    /// <exception cref="InvalidOperationException">The job has no synchronization primitive (it has been returned to the pool).</exception>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.tcs is null)
        {
            ThrowNoSynchronizationPrimitive();
        }

        return this.tcs.Task.WaitAsync(timeout, cancellationToken);
    }

    internal override void _PrepareSynchronizationPrimitive()
    {
        this.tcs ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal override void _SetSynchronizationPrimitive()
    {
        this.tcs?.TrySetResult();
    }

    internal override void _ResetSynchronizationPrimitive()
    {
        this.tcs = default;
    }
}
