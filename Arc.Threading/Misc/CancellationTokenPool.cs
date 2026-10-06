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
    /// <remarks>The caller owns the source exclusively and must return it once when finished.</remarks>
    public static CancellationTokenSource Rent()
        => Pool.Rent();

    /// <summary>
    /// Resets and returns a source to the shared pool, or disposes it if reset fails or the pool is full.
    /// </summary>
    /// <param name="cancellationTokenSource">The <see cref="CancellationTokenSource"/> instance to reset and return to the pool.</param>
    /// <remarks>
    /// Canceled sources cannot be reset and are disposed.
    /// The caller must own the source exclusively, finish all registrations, and stop using its old tokens before returning it.
    /// Return each source only once.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="cancellationTokenSource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The source has already been disposed.</exception>
    public static void Return(CancellationTokenSource cancellationTokenSource)
    {
        ArgumentNullException.ThrowIfNull(cancellationTokenSource);
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
