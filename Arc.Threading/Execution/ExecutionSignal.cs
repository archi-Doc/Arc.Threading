// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Represents the method that handles an <see cref="ExecutionSignal"/> sent to an <see cref="ExecutionCore"/>.
/// </summary>
/// <param name="core">The execution that received the signal.</param>
/// <param name="signal">The received signal.</param>
public delegate void ExecutionSignalHandler(ExecutionCore core, ExecutionSignal signal);

/// <summary>
/// Specifies the signal sent to an <see cref="ExecutionCore"/>.
/// </summary>
public enum ExecutionSignal : byte
{
    /// <summary>
    /// Requests the start of the execution.
    /// </summary>
    Start,

    /// <summary>
    /// Requests cancellation of the execution.
    /// </summary>
    Cancel,

    /// <summary>
    /// Requests termination of the execution.
    /// </summary>
    Terminate,
}
