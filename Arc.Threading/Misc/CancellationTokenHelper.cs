// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using Arc.Collections;

namespace Arc.Threading;

/// <summary>
/// Provides a shared pool of <see cref="CancellationTokenSource"/> instances.
/// </summary>
public static class CancellationTokenPool
{
    /// <summary>
    /// Defines the maximum number of <see cref="CancellationTokenSource"/> instances retained by the shared pool.
    /// </summary>
    public const int PoolCapacity = 256;

    /// <summary>
    /// Provides a shared object pool of <see cref="CancellationTokenSource"/> instances to reduce allocation overhead.
    /// </summary>
    private static readonly ObjectPool<CancellationTokenSource> Pool = new(() => new CancellationTokenSource(), PoolCapacity);

    /// <summary>
    /// Retrieves a <see cref="CancellationTokenSource"/> from the shared object pool.
    /// </summary>
    /// <returns>A <see cref="CancellationTokenSource"/> instance from the pool, which may be new or reused.</returns>
    public static CancellationTokenSource Rent()
        => Pool.Rent();

    /// <summary>
    /// Attempts to reset and return a <see cref="CancellationTokenSource"/> to the shared object pool, or disposes it if reset fails.
    /// </summary>
    /// <param name="cancellationTokenSource">The <see cref="CancellationTokenSource"/> instance to reset and return to the pool.</param>
    /// <remarks>
    /// This method first attempts to reset the <see cref="CancellationTokenSource"/> to its initial state.
    /// If the reset succeeds, the instance is returned to the pool for reuse.
    /// If the reset fails (e.g., the token source has been disposed or is in an invalid state), the instance is disposed instead.
    /// This ensures that only valid, reusable instances are returned to the pool.
    /// </remarks>
    public static void TryResetAndReturn(CancellationTokenSource cancellationTokenSource)
    {
        if (cancellationTokenSource.TryReset())
        {
            Pool.Return(cancellationTokenSource);
        }
        else
        {
            cancellationTokenSource.Dispose();
        }
    }
}
