// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

namespace Arc.Threading;

/// <summary>
/// Represents the return value of a process.<br/>
/// <see cref="AbortOrComplete.Abort"/>: Abort or Error.<br/>
/// <see cref="AbortOrComplete.Complete"/>: Complete.
/// </summary>
public enum AbortOrComplete
{
    /// <summary>
    /// The process has been aborted or an error has occurred.
    /// </summary>
    Abort,

    /// <summary>
    /// The process completed.
    /// </summary>
    Complete,
}
