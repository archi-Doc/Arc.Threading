// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1009 // Closing parenthesis should be spaced correctly

namespace Arc.Threading;

/// <summary>
/// Represents a thread synchronization event that other threads wait until a pulse (signal) is received.
/// </summary>
public class AsyncSinglePulseEvent
{
    private volatile TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Send a pulse to restart the waiting threads.<br/>
    /// The pulse is valid until it is received by a thread.
    /// </summary>
    /// <returns><see langword="true"/>: Success<br/>
    /// <see langword="false"/>: A pulse is already sent (not yet received by a thread).</returns>
    public bool Pulse()
    {
        if (!this.tcs.Task.IsCompleted && this.tcs.TrySetResult())
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a task that will complete when a pulse is received.
    /// </summary>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public async Task WaitAsync()
    {
        await this.tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a task that will complete when a pulse is received or when the specified timeout expires.
    /// </summary>
    /// <param name="timeout">The timeout after which the Task should be faulted with a <see cref="TimeoutException"/> if it hasn't otherwise completed.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public async Task WaitAsync(TimeSpan timeout)
    {
        await this.tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a task that will complete when a pulse is received.
    /// </summary>
    /// <param name="cancellationToken">The CancellationToken to monitor for a cancellation request.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await this.tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a task that will complete when a pulse is received or when the specified timeout expires.
    /// </summary>
    /// <param name="timeout">The timeout after which the Task should be faulted with a <see cref="TimeoutException"/> if it hasn't otherwise completed.</param>
    /// <param name="cancellationToken">The CancellationToken to monitor for a cancellation request.</param>
    /// <returns>The <see cref="Task"/> representing the asynchronous wait.</returns>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await this.tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}
