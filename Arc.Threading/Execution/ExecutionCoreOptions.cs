// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Specifies optional behavior for an <see cref="ExecutionCore"/> instance.
/// </summary>
/// <remarks>
/// The default behavior starts the execution immediately after initialization
/// and disposes the execution core when the execution method exits.<br/>
/// Use these flags to opt out of one or more default behaviors.
/// </remarks>
[Flags]
public enum ExecutionCoreOptions : byte
{
    /// <summary>
    /// Default behavior.<br/>
    /// Starts the execution immediately after the instance is initialized.<br/>
    /// Disposes the execution core when the execution method exits.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Delays starting the execution after the instance is initialized.
    /// </summary>
    DelayedStart = 1 << 0,

    /// <summary>
    /// Keeps the execution core alive when the execution method exits.
    /// </summary>
    KeepAliveOnCompletion = 1 << 1,
}
