// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

public class AsyncManualResetEvent
{
    private volatile TaskCompletionSource tcs = new();

    public Task AsTask => this.tcs.Task;

    public void Set() => this.tcs.TrySetResult();

    public void Reset()
    {
        while (true)
        {
            var tcs = this.tcs;
            if (!tcs.Task.IsCompleted ||
                Interlocked.CompareExchange(ref this.tcs, new TaskCompletionSource(), tcs) == tcs)
            {
                return;
            }
        }
    }
}
