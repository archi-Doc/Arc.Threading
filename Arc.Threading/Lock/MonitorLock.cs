// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Provides a reentrant exclusive lock using <see cref="Monitor"/>.
/// </summary>
/// <remarks>Acquire and release the lock on the same thread. Do not hold it across an await.</remarks>
public class MonitorLock : ILockable
{
    private readonly object syncObject = new();

    /// <summary>
    /// Acquires the lock and returns a scope that releases it on disposal.
    /// </summary>
    /// <returns>A scope that owns this lock on the current thread.</returns>
    public LockScope EnterScope()
        => new LockScope(this);

    /// <summary>
    /// Gets a value indicating whether the current thread holds the exclusive lock.
    /// </summary>
    public bool IsLocked
        => Monitor.IsEntered(this.syncObject);

    /// <summary>
    /// Acquires an exclusive lock.
    /// </summary>
    /// <returns><see langword="true"/> when the lock is acquired.</returns>
    public bool Enter()
    {
        var lockTaken = false;
        Monitor.Enter(this.syncObject, ref lockTaken);
        return lockTaken;
    }

    /// <summary>
    /// Releases the exclusive lock.
    /// </summary>
    /// <exception cref="SynchronizationLockException">The current thread does not own the lock.</exception>
    public void Exit()
    {
        Monitor.Exit(this.syncObject);
    }
}
