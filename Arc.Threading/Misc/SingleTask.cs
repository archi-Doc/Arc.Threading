// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// This class guarantees that only one task will be executed at a time per instance.<br/>
/// When the <see cref="TryRun(Action)"/> or <see cref="TryRun(Func{Task})"/> function is called, it executes if there is no currently running task, and returns <see langword="null"/> if a task is already in progress.
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
    /// Attempts to execute the specified task.<br/>
    /// It executes if there is no currently running task, and returns <see langword="null"/> if a task is already in progress.
    /// </summary>
    /// <param name="task">The work to execute asynchronously.</param>
    /// <returns>Returns a valid task instance if there is no currently running Task.<br/>
    /// <see langword="null"/> if a task is already in progress.<br/>
    /// The returned task faults if the work throws an exception.</returns>
    public Task? TryRun(Action task)
    {
        if (Interlocked.CompareExchange(ref this.running, 1, 0) != 0)
        {
            return default;
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref this.task, completionSource.Task);
        _ = this.RunAsync(Task.Run(task), completionSource);
        return completionSource.Task;
    }

    /// <summary>
    /// Attempts to execute the specified task.<br/>
    /// It executes if there is no currently running task, and returns <see langword="null"/> if a task is already in progress.
    /// </summary>
    /// <param name="task">The work to execute asynchronously.</param>
    /// <returns>Returns a valid task instance if there is no currently running Task.<br/>
    /// <see langword="null"/> if a task is already in progress.<br/>
    /// The returned task faults if the work throws an exception.</returns>
    public Task? TryRun(Func<Task> task)
    {
        if (Interlocked.CompareExchange(ref this.running, 1, 0) != 0)
        {
            return default;
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref this.task, completionSource.Task);
        _ = this.RunAsync(Task.Run(task), completionSource);
        return completionSource.Task;
    }

    private async Task RunAsync(Task work, TaskCompletionSource completionSource)
    {
        Exception? exception = default;
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        // Release the instance before completing the task, so that the continuation can start the next task.
        Volatile.Write(ref this.task, default);
        Volatile.Write(ref this.running, 0);

        if (exception is null)
        {
            completionSource.TrySetResult();
        }
        else
        {
            completionSource.TrySetException(exception);
        }
    }

    private int running;
    private Task? task;
}
