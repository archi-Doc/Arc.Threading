// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace Arc.Threading;

/// <summary>
/// Represents a state of a task work.<br/>
/// Created -> Standby -> Abort / Working -> Completed.
/// </summary>
public enum TaskWorkState : int
{
    /// <summary>
    /// Work is created.
    /// </summary>
    Created,

    /// <summary>
    /// Work is on standby.
    /// </summary>
    Standby,

    /// <summary>
    /// Work in progress.
    /// </summary>
    Working,

    /// <summary>
    /// Work is complete (worker -> user).
    /// </summary>
    Complete,

    /// <summary>
    /// Work is aborted (user -> worker).
    /// </summary>
    Aborted,
}

internal static class TaskWorkHelper
{
    internal static TaskWorkState IntToState(int state) => Unsafe.As<int, TaskWorkState>(ref state);

    internal static int StateToInt(TaskWorkState state) => Unsafe.As<TaskWorkState, int>(ref state);
}
