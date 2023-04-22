// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents a ambient execution id(<see cref="long"/>) that is local to a given asynchronous control flow.
/// </summary>
public static class ExecutionId
{
    private static readonly AsyncLocal<long> AsyncLocalInstance = new();
    private static long currentId;

    /// <summary>
    /// Gets an execution for this asynchronous control flow.<br/>
    /// Note that although collisions are very rare, identifiers are not guaranteed to be unique.
    /// </summary>
    /// <returns>The identifier.</returns>
    public static long Get()
    {
        var id = AsyncLocalInstance.Value;
        if (id != 0)
        {
            return id;
        }
        else
        {
            do
            {
                id = Interlocked.Increment(ref currentId);
            }
            while (id == 0);

            AsyncLocalInstance.Value = id;
            return id;
        }
    }
}
