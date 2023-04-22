// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

public struct LockStruct : IDisposable
{
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

    public void Dispose()
    {
        if (this.locked)
        {
            this.lockableObject.Exit();
            this.locked = false;
        }
    }

    public ILockable LockableObject => this.lockableObject;

    public bool IsLocked => this.locked;

    private readonly ILockable lockableObject;
    private bool locked;
}
