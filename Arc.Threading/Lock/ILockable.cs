// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Provides synchronous acquisition and release of an exclusive lock.
/// </summary>
public interface ILockable
{
    /// <summary>
    /// Acquires the lock and returns a scope that releases it on disposal.
    /// </summary>
    /// <returns>A scope representing the acquisition result.</returns>
    LockScope EnterScope() => new LockScope(this);

    /// <summary>
    /// Acquires an exclusive lock.
    /// </summary>
    /// <returns><see langword="true"/> if the lock is acquired; otherwise, <see langword="false"/>.</returns>
    bool Enter();

    /// <summary>
    /// Releases the exclusive lock.
    /// </summary>
    /// <exception cref="SynchronizationLockException">The lock is not held, or a thread-affine lock is owned by another thread.</exception>
    void Exit();

    /// <summary>
    /// Gets a value indicating whether the exclusive lock has been acquired.
    /// </summary>
    /// <remarks>Thread-affine implementations report ownership by the current thread.</remarks>
    bool IsLocked { get; }
}
