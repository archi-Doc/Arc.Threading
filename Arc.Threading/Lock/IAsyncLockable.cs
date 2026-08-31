// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Represents an object that provides an exclusive lock (synchronous and asynchronous).
/// </summary>
public interface IAsyncLockable : ILockable
{
    /// <summary>
    /// Asynchronously acquires an exclusive lock and creates a <see cref="LockStruct"/> for a using statement.
    /// </summary>
    /// <returns><see cref="LockStruct"/>.</returns>
    async Task<LockStruct> EnterScopeAsync()
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
