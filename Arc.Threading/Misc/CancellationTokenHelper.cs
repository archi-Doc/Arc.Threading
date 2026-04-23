// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using Arc.Collections;

namespace Arc.Threading;

public static class CancellationTokenHelper
{
    public const int CtsPoolCapacity = 256;

    public static readonly ObjectPool<CancellationTokenSource> CtsPool = new(() => new CancellationTokenSource(), CtsPoolCapacity);
}
