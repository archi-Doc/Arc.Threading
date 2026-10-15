// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Represents the lifecycle state of a reusable job handled by <see cref="ReusableJobWorker{TJob}"/>.
/// </summary>
/// <remarks>
/// Typical progression is <see cref="Initial"/> → <see cref="Pending"/> → <see cref="Running"/> → <see cref="Completed"/>.<br/>
/// A pending or running job becomes <see cref="Aborted"/> instead of <see cref="Completed"/> when it is not processed or fails.
/// A completed or aborted job becomes <see cref="Pooled"/> when it is returned.
/// </remarks>
public enum ReusableJobState : byte
{
    /// <summary>
    /// The job has been created or rented, but not yet queued.
    /// </summary>
    Initial,

    /// <summary>
    /// Waiting state. The job is queued and waiting to be processed.
    /// </summary>
    Pending,

    /// <summary>
    /// Processing state. The job is currently being executed.
    /// </summary>
    Running,

    /// <summary>
    /// Completed state. The job has finished execution.
    /// </summary>
    Completed,

    /// <summary>
    /// Processing was skipped because the worker stopped, processing threw an exception, or the completion callback threw an exception.
    /// </summary>
    Aborted,

    /// <summary>
    /// Pooled state. The job instance has been returned to the reusable pool and is available for future scheduling.
    /// </summary>
    Pooled,
}
