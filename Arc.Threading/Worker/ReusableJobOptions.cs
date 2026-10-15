// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Defines options that control the behavior of reusable job instances.<br/>
/// This enumeration supports bitwise combination of its member values.
/// </summary>
[Flags]
public enum ReusableJobOptions : byte
{
    /// <summary>
    /// No options are set.
    /// </summary>
    None = 0,

    /// <summary>
    /// Automatically returns a completed or aborted job to the pool. Do not await or access it after submission.<br/>
    /// A pooled <see cref="ReusableTaskJob"/> rented with this option has no completion source, so its wait members throw <see cref="InvalidOperationException"/>.
    /// </summary>
    ReturnToPoolOnCompletion = 1 << 1,
}
