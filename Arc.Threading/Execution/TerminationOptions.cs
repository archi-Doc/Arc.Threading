// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Specifies optional behavior for termination and wait operations of an <see cref="ExecutionCore"/>.<br/>
/// By default, descendants marked as <see cref="ExecutionCore.IsIndependent"/> and their subtrees are excluded; the target itself is included.
/// </summary>
[Flags]
public enum TerminationOptions : byte
{
    /// <summary>
    /// No options are set. Independent descendants and their subtrees are excluded.
    /// </summary>
    None = 0,

    /// <summary>
    /// Includes independent descendants and their subtrees in the termination or wait target.
    /// </summary>
    IncludeIndependent = 1 << 0,
}
