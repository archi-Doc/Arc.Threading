// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents an ambient execution id (<see cref="long"/>) that is local to a given asynchronous control flow.
/// </summary>
public static class ExecutionId
{
    private static readonly AsyncLocal<long> AsyncLocalInstance = new();
    private static long currentId;

    /// <summary>
    /// Gets the execution id of the current asynchronous control flow.<br/>
    /// A new id is assigned on the first call, and the same id is returned within the flow.<br/>
    /// Note that the id is unique only within the process lifetime, and may be shared with control flows that were forked after the id was assigned.
    /// </summary>
    /// <returns>The identifier.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Get()
    {
        var id = AsyncLocalInstance.Value;
        if (id != 0)
        {
            return id;
        }
        else
        {
            return NewId();
        }
    }

    private static long NewId()
    {
        long id;
        do
        {
            id = Interlocked.Increment(ref currentId);
        }
        while (id == 0);

        AsyncLocalInstance.Value = id;
        return id;
    }
}
