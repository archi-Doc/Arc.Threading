// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Provides an exclusive lock with synchronous and asynchronous acquisition.
/// </summary>
public interface IAsyncLockable : ILockable
{
    /// <summary>
    /// Asynchronously acquires the lock and returns a scope that releases it on disposal.
    /// </summary>
    /// <returns>A scope representing the acquisition result.</returns>
    async Task<LockScope> EnterScopeAsync()
    {
        var lockTaken = await this.EnterAsync().ConfigureAwait(false);
        return new(this, lockTaken);
    }

    /// <summary>
    /// Asynchronously waits to acquire an exclusive lock.
    /// </summary>
    /// <returns><see langword="true"/> if the lock is acquired; otherwise, <see langword="false"/>.</returns>
    Task<bool> EnterAsync();
}
