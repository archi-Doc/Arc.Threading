// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Holds an exclusive lock until the scope is disposed.
/// </summary>
/// <remarks>Do not copy an acquired scope; each copy has its own ownership flag.</remarks>
public struct LockScope : IDisposable
{
    private readonly ILockable lockable;
    private bool locked;

    /// <summary>
    /// Initializes a new instance of the <see cref="LockScope"/> struct, and acquires the exclusive lock.
    /// </summary>
    /// <param name="lockable">The object to lock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lockable"/> is <see langword="null"/>.</exception>
    public LockScope(ILockable lockable)
    {
        ArgumentNullException.ThrowIfNull(lockable);
        this.lockable = lockable;
        this.locked = lockable.Enter();
    }

    internal LockScope(ILockable lockable, bool locked)
    {
        this.lockable = lockable;
        this.locked = locked;
    }

    /// <summary>
    /// Gets the object associated with this lock scope.
    /// </summary>
    public ILockable Lockable => this.lockable;

    /// <summary>
    /// Gets a value indicating whether this scope currently holds the exclusive lock.
    /// </summary>
    public bool IsLocked => this.locked;

    /// <summary>
    /// Releases the exclusive lock if held. Repeated disposal of this instance has no effect.
    /// </summary>
    public void Dispose()
    {
        if (this.locked)
        {
            this.lockable.Exit();
            this.locked = false;
        }
    }
}
