// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Defines flags that control the behavior of reusable job instances.<br/>
/// This enumeration supports bitwise combination of its member values.
/// </summary>
[Flags]
public enum ReusableJobFlags : byte
{
    /// <summary>
    /// No flags are set.
    /// </summary>
    None = 0,

    /// <summary>
    /// Automatically returns the job object to the pool when the job completes.
    /// </summary>
    ReturnToPoolOnCompletion = 1 << 1,
}
