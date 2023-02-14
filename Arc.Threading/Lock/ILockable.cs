// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

public interface ILockable
{
    LockStruct Lock() => new LockStruct(this);

    bool Enter();

    void Exit();

    bool IsLocked { get; }
}
