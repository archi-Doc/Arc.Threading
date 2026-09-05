// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents an object that provides an exclusive lock (synchronous).
/// </summary>
public interface ILockable
{
    /// <summary>
    /// Acquires the lock and returns a scope that releases it on disposal.
    /// </summary>
    /// <returns><see cref="LockStruct"/>.</returns>
    LockStruct EnterScope() => new LockStruct(this);

    /// <summary>
    /// Acquires an exclusive lock.
    /// </summary>
    /// <returns><see langword="true"/>; the lock is acquired.</returns>
    bool Enter();

    /// <summary>
    /// Releases the exclusive lock.
    /// </summary>
    /// <exception cref="SynchronizationLockException">The current thread does not own the lock.</exception>
    void Exit();

    /// <summary>
    /// Gets a value indicating whether the exclusive lock has been acquired.
    /// </summary>
    bool IsLocked { get; }
}
