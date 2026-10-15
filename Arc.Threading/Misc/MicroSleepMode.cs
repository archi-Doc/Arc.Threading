// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Specifies the sleep method used by a <see cref="MicroSleep"/> instance.
/// </summary>
public enum MicroSleepMode
{
    /// <summary>
    /// The <see cref="MicroSleep"/> instance has been disposed.
    /// </summary>
    Disposed,

    /// <summary>
    /// Uses the POSIX nanosleep function (non-Windows platforms).
    /// </summary>
    Nanosleep,

    /// <summary>
    /// Uses a high-resolution waitable timer created by CreateWaitableTimerEx (Windows).
    /// </summary>
    WaitableTimerEx,

    /// <summary>
    /// Uses <see cref="System.Threading.Thread.Sleep(int)"/> after raising the system timer resolution to 1 ms with timeBeginPeriod (Windows fallback).
    /// Durations are rounded up to whole milliseconds.
    /// </summary>
    TimeBeginPeriod,
}
