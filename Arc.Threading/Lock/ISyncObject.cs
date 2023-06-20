// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

public interface ISyncObject
{
    /// <summary>
    /// Gets the object on which to acquire the monitor lock.
    /// </summary>
    object SyncObject { get; }
}
