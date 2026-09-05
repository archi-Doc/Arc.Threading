// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

/// <summary>
/// Represents an exclusive lock scope of an <see cref="ILockable"/> object.<br/>
/// The lock is released when this instance is disposed (using statement).
/// </summary>
/// <remarks>Do not copy an acquired scope; each copy has its own ownership flag.</remarks>
public struct LockStruct : IDisposable
{
    private readonly ILockable lockableObject;
    private bool locked;

    /// <summary>
    /// Initializes a new instance of the <see cref="LockStruct"/> struct, and acquires the exclusive lock.
    /// </summary>
    /// <param name="lockableObject">The object to lock.</param>
    public LockStruct(ILockable lockableObject)
    {
        this.lockableObject = lockableObject;
        this.locked = lockableObject.Enter();
    }

    internal LockStruct(ILockable lockableObject, bool locked)
    {
        this.lockableObject = lockableObject;
        this.locked = locked;
    }

    /// <summary>
    /// Gets the object associated with this lock scope.
    /// </summary>
    public ILockable LockableObject => this.lockableObject;

    /// <summary>
    /// Gets a value indicating whether this scope currently holds the exclusive lock.
    /// </summary>
    public bool IsLocked => this.locked;

    /// <summary>
    /// Releases the exclusive lock if it is held.
    /// </summary>
    public void Dispose()
    {
        if (this.locked)
        {
            this.lockableObject.Exit();
            this.locked = false;
        }
    }
}
