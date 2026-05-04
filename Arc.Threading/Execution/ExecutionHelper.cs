// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
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

    [DoesNotReturn]
    internal static void ThrowDifferentRootException()
        => throw new InvalidOperationException("The stack and parent objects must be created from the same Root.");

    [DoesNotReturn]
    internal static void ThrowDifferentParentException()
        => throw new InvalidOperationException("The parent and child objects must be created from the same Root.");
}
