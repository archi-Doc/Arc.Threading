// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Represents a reusable job that uses task-based asynchronous waiting.
/// </summary>
public record class ReusableTaskJob : ReusableJob
{
    private TaskCompletionSource? tcs;

    public ReusableTaskJob()
    {
    }

    /// <summary>
    /// Gets the <see cref="System.Threading.Tasks.Task"/> associated with this reusable job.
    /// </summary>
    /// <value>
    /// The task that will complete when the job is done.
    /// </value>
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
    /// Asynchronously waits until this job is completed.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the wait operation.</param>
    /// <returns>
    /// A task that completes when the job is set, or is canceled if <paramref name="cancellationToken"/> is canceled.
    /// </returns>
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
    /// <param name="timeout">The maximum time to wait before timing out. </param>
    /// <param name="cancellationToken">A token used to cancel the wait operation.</param>
    /// <returns>
    /// A task that completes when the job is set, or is canceled if <paramref name="cancellationToken"/> is canceled.
    /// </returns>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.tcs is null)
        {
            ThrowNoSynchronizationPrimitive();
        }

        return this.tcs.Task.WaitAsync(timeout, cancellationToken);
    }

    internal override void _InitializeSynchronizationPrimitive()
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
