// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using Arc.Collections;

namespace Arc.Threading;

public static class CancellationTokenHelper
{
    /// <summary>
    /// Defines the maximum number of <see cref="CancellationTokenSource"/> instances retained by the shared pool.
    /// </summary>
    public const int PoolCapacity = 256;

    /// <summary>
    /// Provides a shared object pool of <see cref="CancellationTokenSource"/> instances to reduce allocation overhead.
    /// </summary>
    public static readonly ObjectPool<CancellationTokenSource> Pool = new(() => new CancellationTokenSource(), PoolCapacity);
}
