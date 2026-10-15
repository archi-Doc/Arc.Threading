// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Specifies startup and disposal behavior for a <see cref="TaskCore"/> or <see cref="ThreadCore"/> instance.
/// </summary>
/// <remarks>
/// The default behavior starts the execution immediately after initialization
/// and disposes the execution core when the execution method exits, or when a start is skipped because the core was already canceled.<br/>
/// Use these flags to opt out of one or more default behaviors.
/// </remarks>
[Flags]
public enum ExecutionCoreOptions : byte
{
    /// <summary>
    /// No options are set (default behavior).<br/>
    /// Starts the execution immediately after the instance is initialized.<br/>
    /// Disposes the execution core when the execution method exits or its start is skipped.
    /// </summary>
    None = 0,

    /// <summary>
    /// Waits for <see cref="ExecutionSignal.Start"/> before starting the execution.
    /// A delayed core that never receives the signal is not disposed automatically, even if it is canceled; its owner must dispose it.
    /// </summary>
    DelayedStart = 1 << 0,

    /// <summary>
    /// Does not dispose the execution core automatically when the execution method exits or its start is skipped.
    /// The owner must dispose the core.
    /// </summary>
    NoDisposeOnCompletion = 1 << 1,
}
