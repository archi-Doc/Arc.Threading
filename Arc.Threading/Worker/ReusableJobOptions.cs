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
    /// Automatically returns a completed or aborted job to the pool. Do not await or access it after submission.
    /// </summary>
    ReturnToPoolOnCompletion = 1 << 1,
}
