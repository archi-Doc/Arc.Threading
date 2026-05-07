// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;

namespace Arc.Threading;

[Flags]
public enum TerminationOptions : byte
{
    /// <summary>
    /// Include independent elements in the termination or wait target.
    /// </summary>
    IncludeIndependent = 1 << 0,
}
