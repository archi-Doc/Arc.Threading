// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

public interface IAsyncLockable : ILockable
{
    async Task<LockStruct> LockAsync()
    {
        var lockTaken = await this.EnterAsync().ConfigureAwait(false);
        return new(this, lockTaken);
    }

    Task<bool> EnterAsync();
}
