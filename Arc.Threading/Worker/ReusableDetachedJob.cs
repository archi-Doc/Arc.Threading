// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Represents a reusable job that is always detached from caller-side completion waiting.
/// </summary>
public record class ReusableDetachedJob : ReusableJobBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableDetachedJob"/> class.
    /// </summary>
    public ReusableDetachedJob()
    {
    }

    internal override void _InitializeSynchronizationPrimitive()
    {
    }

    internal override void _SetSynchronizationPrimitive()
    {
    }

    internal override void _ResetSynchronizationPrimitive()
    {
    }
}
