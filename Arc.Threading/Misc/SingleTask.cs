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
    public SingleTask()
    {
    }

    /// <summary>
    /// Attempts to execute the specified task.<br/>
    /// It executes if there is no currently running task, and returns <see langword="null"/> if a task is already in progress.
    /// </summary>
    /// <param name="task">The work to execute asynchronously.</param>
    /// <returns>Returns a valid task instance if there is no currently running Task.<br/>
    /// <see langword="null"/> if a task is already in progress.</returns>
    public Task? TryRun(Action task)
    {
        if (Interlocked.CompareExchange(ref this.running, 1, 0) != 0)
        {
            return default;
        }

        var t = Task.Run(task).ContinueWith(x =>
        {
            this.task = default;
            this.running = 0;
        });

        this.task = t;
        return t;
    }

    /// <summary>
    /// Attempts to execute the specified task.<br/>
    /// It executes if there is no currently running task, and returns <see langword="null"/> if a task is already in progress.
    /// </summary>
    /// <param name="task">The work to execute asynchronously.</param>
    /// <returns>Returns a valid task instance if there is no currently running Task.<br/>
    /// <see langword="null"/> if a task is already in progress.</returns>
    public Task? TryRun(Func<Task> task)
    {
        if (Interlocked.CompareExchange(ref this.running, 1, 0) != 0)
        {
            return default;
        }

        var t = Task.Run(task).ContinueWith(x =>
        {
            this.task = default;
            this.running = 0;
        });

        this.task = t;
        return t;
    }

    public Task? RunningTask
        => this.task;

    private int running;
    private Task? task;
}
