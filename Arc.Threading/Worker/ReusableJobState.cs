// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Represents the lifecycle state of a reusable job handled by <see cref="ReusableJobWorker{TJob}"/>.
/// </summary>
/// <remarks>
/// Typical progression is <see cref="Initial"/> → <see cref="Pending"/> → <see cref="Running"/> → <see cref="Completed"/>.
/// </remarks>
public enum ReusableJobState : byte
{
    /// <summary>
    /// Initial state. The job has been created but not yet queued.
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
    /// Aborted state. The job was terminated before completion.
    /// </summary>
    Aborted,
}
