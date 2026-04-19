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
    /// Gets a value indicating whether the object is automatically returned when the job completes.<br/>
    /// Enable this when you do not use the job's return value (fire-and-forget pattern).
    /// </summary>
    public bool AutoReturnOnJobCompletion { get; internal set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJobBase"/> class.
    /// </summary>
    public ReusableJobBase()
    {
    }

    /// <summary>
    /// Resets the job to its initial state, allowing it to be reused for another execution.<br/>
    /// This method is intended to be overridden by derived classes to reset custom user-defined state.<br/>
    /// The base implementation is empty and does nothing.
    /// </summary>
    public virtual void Reset()
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
