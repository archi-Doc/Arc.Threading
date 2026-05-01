// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

public delegate void ExecutionSignalHandler(ExecutionCore executionCore, ExecutionSignal executionSignal);

public enum ExecutionSignal : byte
{
    /// <summary>
    /// Requests cancellation of the execution.
    /// </summary>
    Cancel,

    /// <summary>
    /// Requests termination of the execution.
    /// </summary>
    Exit,
}
