// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Represents the base record class for reusable jobs that can be executed by a worker.<br/>
/// Since this class does not provide a way to wait for completion, inherit from <br/>
/// <see cref="ReusableThreadJob" /> (ManualResetEventSlim-based, recommended) or <see cref="ReusableTaskJob" /> (TaskCompletionSource-based).
/// </summary>
public abstract record class ReusableJobBase
{
    /// <summary>
    /// Gets the current state of the reusable job.
    /// </summary>
    public ReusableJobState State { get; internal set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJobBase"/> class.
    /// </summary>
    public ReusableJobBase()
    {
    }

    /// <summary>
    /// Sets the internal state when the job is being prepared for execution.
    /// </summary>
    internal abstract void SetInternal();

    /// <summary>
    /// Resets the internal state when the job execution is complete or cancelled.
    /// </summary>
    internal abstract void ResetInternal();
}
