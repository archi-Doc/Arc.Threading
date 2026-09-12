// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents an object that provides a locking mechanism.
/// </summary>
public interface ILockObject
{
    /// <summary>
    /// Gets the <see cref="Lock"/> object used for synchronization.
    /// </summary>
    Lock LockObject { get; }
}
