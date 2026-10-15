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
/// <remarks>Task and thread cores handle only <see cref="Start"/>, and groups forward every signal to their children by default. Other signals require a custom handler; use <see cref="ExecutionCore.RequestTermination(TerminationOptions)"/> for cancellation.</remarks>
public enum ExecutionSignal : byte
{
    /// <summary>
    /// Requests the start of the execution.<br/>
    /// A task or thread core starts at most once; the signal is required when it was created with <see cref="ExecutionCoreOptions.DelayedStart"/>.
    /// </summary>
    Start,

    /// <summary>
    /// Delivers an application-defined cancellation signal. It does not cancel the execution by itself.
    /// </summary>
    Cancel,

    /// <summary>
    /// Delivers an application-defined termination signal. It does not terminate the execution by itself.
    /// </summary>
    Terminate,
}
