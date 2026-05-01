// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading;

namespace Arc.Threading;

public static class ExecutionHelper
{
    public static ExecutionCore? ExtractCore(this CancellationToken cancellationToken)
    {// In my opinion, CancellationToken should have been named something like TaskContext, with added features for managing parent-child dependencies and for canceling or terminating processing.
        try
        {
            var cts = Unsafe.As<CancellationToken, CancellationTokenSource>(ref cancellationToken);
            return cts as ExecutionCore;
        }
        catch
        {
            return null;
        }
    }
}
