// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1009 // Closing parenthesis should be spaced correctly

namespace Arc.Threading;

public class AsyncPulseEvent
{
    private volatile TaskCompletionSource<object> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Pulse()
    {
        if (!this.tcs.Task.IsCompleted &&
            this.tcs.TrySetResult(new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously)))
        {
            return true;
        }

        return false;
    }

    public async Task WaitAsync()
    {
        this.tcs = (TaskCompletionSource<object>)await this.tcs.Task.ConfigureAwait(false);
    }

    public async Task WaitAsync(TimeSpan timeout)
    {
        this.tcs = (TaskCompletionSource<object>)await this.tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        this.tcs = (TaskCompletionSource<object>)await this.tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        this.tcs = (TaskCompletionSource<object>)await this.tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}
