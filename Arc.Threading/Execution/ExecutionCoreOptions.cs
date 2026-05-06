// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

[Flags]
public enum ExecutionCoreOptions
{
    /// <summary>
    /// Starts the thread immediately after the instance is initialized.
    /// </summary>
    StartImmediately = 1 << 0,

    /// <summary>
    /// Disposes the ThreadCore instance when the thread method exits.
    /// </summary>
    DisposeOnCompletion = 1 << 1,

    /// <summary>
    /// Default behavior.<br/>
    /// Starts the thread immediately after the instance is initialized.<br/>
    /// Disposes the ThreadCore instance when the thread method exits.
    /// </summary>
    Default = StartImmediately | DisposeOnCompletion,
}
