// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Represents the lifecycle state of a reusable job worker.
/// </summary>
public enum ReusableJobWorkerState : byte
{
    /// <summary>
    /// The worker is initialized and ready to accept a job, but is not currently executing one.
    /// </summary>
    Idle,

    /// <summary>
    /// The worker is currently executing a job.
    /// </summary>
    Working,

    /// <summary>
    /// The worker has been terminated and is no longer available for job execution.
    /// </summary>
    Terminated,
}
