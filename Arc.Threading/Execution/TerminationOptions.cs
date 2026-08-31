// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Specifies optional behavior for termination and wait operations of an <see cref="ExecutionCore"/>.<br/>
/// By default, executions marked as <see cref="ExecutionCore.IsIndependent"/> are excluded.
/// </summary>
[Flags]
public enum TerminationOptions : byte
{
    /// <summary>
    /// Include independent elements in the termination or wait target.
    /// </summary>
    IncludeIndependent = 1 << 0,
}
