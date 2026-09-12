// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Specifies the sleep method used by a <see cref="MicroSleep"/> instance.
/// </summary>
public enum MicroSleepMode
{
    /// <summary>
    /// MicroSleep instance has been disposed.
    /// </summary>
    Disposed,

    /// <summary>
    /// MicroSleep instance uses the nanosleep method for sleep operations.
    /// </summary>
    Nanosleep,

    /// <summary>
    /// MicroSleep instance uses the WaitableTimerEx method for sleep operations.
    /// </summary>
    WaitableTimerEx,

    /// <summary>
    /// MicroSleep instance uses the timeBeginPeriod method for sleep operations.
    /// </summary>
    TimeBeginPeriod,
}
