// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Runs at most one operation at a time on the thread pool. Overlapping calls return <see langword="null"/>.
/// </summary>
public class SingleTask
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SingleTask"/> class.
    /// </summary>
    public SingleTask()
    {
    }

    /// <summary>
    /// Gets the task that is currently in progress, or <see langword="null"/> if no task is running.
    /// </summary>
    public Task? RunningTask
        => Volatile.Read(ref this.task);

    /// <summary>
    /// Schedules the action if no operation is running.
    /// </summary>
    /// <param name="action">The work to execute asynchronously.</param>
    /// <returns>The action's completion task, or <see langword="null"/> if an operation is already running. Failures propagate through the task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    public Task? TryRun(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Interlocked.CompareExchange(ref this.running, 1, 0) != 0)
        {
            return default;
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref this.task, completionSource.Task);
        _ = this.RunAsync(Task.Run(action), completionSource);
        return completionSource.Task;
    }

    /// <summary>
    /// Schedules the asynchronous action if no operation is running.
    /// </summary>
    /// <param name="asyncAction">The asynchronous work to execute.</param>
    /// <returns>The action's completion task, or <see langword="null"/> if an operation is already running. Faults and cancellation are preserved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asyncAction"/> is <see langword="null"/>.</exception>
    public Task? TryRun(Func<Task> asyncAction)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);
        if (Interlocked.CompareExchange(ref this.running, 1, 0) != 0)
        {
            return default;
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref this.task, completionSource.Task);
        _ = this.RunAsync(Task.Run(asyncAction), completionSource);
        return completionSource.Task;
    }

    private async Task RunAsync(Task work, TaskCompletionSource completionSource)
    {
        await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Release the instance before completing the task, so that the continuation can start the next task.
        Volatile.Write(ref this.task, default);
        Volatile.Write(ref this.running, 0);

        // Propagate the exact outcome: all exceptions, or cancellation (rather than a fault wrapping it).
        completionSource.TrySetFromTask(work);
    }

    private int running;
    private Task? task;
}
