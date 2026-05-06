// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

[Flags]
public enum RequestTerminationOptions
{
    /*/// <summary>
    /// Terminates only this instance. Child elements are not terminated.
    /// </summary>
    SelfOnly = 1 << 0,*/

    /// <summary>
    /// Terminate independent elements.
    /// </summary>
    IncludeIndependent = 1 << 1,
}
