// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;

namespace Arc.Threading;

/// <summary>
/// <see cref="MonitorLock"/> class implements <see cref="ILockable"/>, which is actually a wrapper class for an object and <see cref="Monitor"/> methods.
/// </summary>
public class MonitorLock : ILockable
{
    private readonly object syncObject = new();

    /// <summary>
    /// Acquires an exclusive lock and creates a <see cref="LockStruct"/> for a using statement.
    /// </summary>
    /// <returns><see cref="LockStruct"/>.</returns>
    public LockStruct EnterScope()
        => new LockStruct(this);

    /// <summary>
    /// Gets a value indicating whether the current thread holds the exclusive lock.
    /// </summary>
    public bool IsLocked
        => Monitor.IsEntered(this.syncObject);

    /// <summary>
    /// Acquires an exclusive lock.
    /// </summary>
    /// <returns><see langword="true"/>; the lock is acquired.</returns>
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
