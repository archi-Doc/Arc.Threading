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
    /// <remarks>The caller owns the source exclusively. When finished, pass it to <see cref="Return"/> once instead of disposing it.</remarks>
    public static CancellationTokenSource Rent()
        => Pool.Rent();

    /// <summary>
    /// Resets a source and returns it to the shared pool, or disposes it if it cannot be pooled.
    /// </summary>
    /// <param name="cancellationTokenSource">The <see cref="CancellationTokenSource"/> instance to reset and return to the pool.</param>
    /// <remarks>
    /// The source is disposed instead of pooled if it is canceled (it cannot be reset), if it is linked or of a derived type
    /// (a linked source stays subscribed to its parent tokens), or if the pool is full.<br/>
    /// Resetting removes any remaining registrations. The caller must own the source exclusively, finish all registrations,
    /// and stop using its old tokens before returning it. Return each source only once.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="cancellationTokenSource"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">A source whose exact type is <see cref="CancellationTokenSource"/> has already been disposed.</exception>
    public static void Return(CancellationTokenSource cancellationTokenSource)
    {
        ArgumentNullException.ThrowIfNull(cancellationTokenSource);
        if (cancellationTokenSource.GetType() == typeof(CancellationTokenSource) &&
            cancellationTokenSource.TryReset())
        {
            Pool.Return(cancellationTokenSource);
        }
        else
        {
            cancellationTokenSource.Dispose();
        }
    }
}
