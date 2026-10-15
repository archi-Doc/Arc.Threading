// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Provides an ambient execution id (<see cref="long"/>) that is local to a given asynchronous control flow.
/// </summary>
public static class ExecutionId
{
    private static readonly AsyncLocal<long> AsyncLocalInstance = new();
    private static long currentId;

    /// <summary>
    /// Gets the execution id of the current asynchronous control flow.<br/>
    /// A new id is assigned on the first call, and the same id is returned within the flow.
    /// </summary>
    /// <returns>A nonzero id. Newly assigned ids are sequential and unique within the process lifetime.</returns>
    /// <remarks>
    /// The id is shared with control flows that were forked after it was assigned (for example, tasks started later).<br/>
    /// An id first assigned in a child flow (for example, inside an awaited async method) does not flow back to the caller;
    /// call this method at the start of the logical operation so that nested calls share the id.
    /// </remarks>
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
