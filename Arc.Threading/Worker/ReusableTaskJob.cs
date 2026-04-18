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
    private TaskCompletionSource tcs;

    public ReusableTaskJob()
    {
        this.tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Gets the <see cref="System.Threading.Tasks.Task"/> associated with this reusable job.
    /// </summary>
    /// <value>
    /// The task that will complete when the job is done.
    /// </value>
    public Task Task => this.tcs.Task;

    internal override void SetInternal()
    {
        this.tcs.SetResult();
    }

    internal override void ResetInternal()
    {
        this.tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
