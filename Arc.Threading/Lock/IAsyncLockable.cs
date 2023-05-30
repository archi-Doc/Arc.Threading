// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

public interface IAsyncLockable : ILockable
{
    /// <summary>
    /// Create a <see cref="LockStruct"/> from an <see cref="ILockable"/> object for using statement.
    /// </summary>
    /// <returns><see cref="LockStruct"/>.</returns>
    async Task<LockStruct> LockAsync()
    {
        var lockTaken = await this.EnterAsync().ConfigureAwait(false);
        return new(this, lockTaken);
    }

    /// <summary>
    /// Asynchronously waits to acquire an exclusive lock.
    /// </summary>
    /// <returns><see langword="true"/>; the lock is acquired.</returns>
    Task<bool> EnterAsync();
}
