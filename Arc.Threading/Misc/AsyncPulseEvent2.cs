// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

public class AsyncPulseEvent2
{
    private TaskCompletionSource<bool>? waiter;

    public AsyncPulseEvent2()
    {
        this.waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Pulse()
    {
        var waiter = Interlocked.Exchange(ref this.waiter, null);
        waiter?.TrySetResult(true);
    }

    public Task Wait()
    {
        return this.Wait(CancellationToken.None);
    }

    public Task Wait(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var prev = Interlocked.CompareExchange(ref this.waiter, tcs, null);
        if (prev is not null)
        {
            throw new InvalidOperationException();
        }

        if (cancellationToken.CanBeCanceled)
        {
            var reg = cancellationToken.Register(() =>
            {
                var removed = Interlocked.CompareExchange(ref this.waiter, null, tcs);
                if (removed == tcs)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
            });

            tcs.Task.ContinueWith(
                (_, r) => ((CancellationTokenRegistration)r!).Dispose(),
                reg,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return tcs.Task;
    }
}
