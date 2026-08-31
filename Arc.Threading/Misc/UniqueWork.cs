// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// <see cref="UniqueWork"/> represents a unique work (number of concurrent processes is 1).<br/>
/// For a work that is invoked multiple times by multiple threads, but the work is executed only once simultaneously.
/// </summary>
public class UniqueWork
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UniqueWork"/> class.
    /// </summary>
    /// <param name="action">The work to execute.</param>
    public UniqueWork(Action action)
    {
        this.workAction = action;
        this.PrepareNextTask();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UniqueWork"/> class.
    /// </summary>
    /// <param name="task">The asynchronous work to execute.</param>
    public UniqueWork(Func<Task> task)
    {
        this.workTask = task;
        this.PrepareNextTask();
    }

    /// <summary>
    /// Starts the work, or joins the work which is already in progress.
    /// </summary>
    /// <returns>The task of the work being executed.</returns>
    public Task Run()
    {
        var next = Volatile.Read(ref this.nextTask);
        var original = Interlocked.CompareExchange(ref this.currentTask, next, null);
        if (original is not null)
        {// The work is already in progress.
            return original;
        }

        // Prepare the next task and start the current one.
        // Note that the current task may complete (and clear this.currentTask) immediately, so return the local variable.
        this.PrepareNextTask();
        next.Start();
        return next;
    }

    [MemberNotNull(nameof(nextTask))]
    private void PrepareNextTask()
    {
        this.nextTask = new Task(() =>
        {
            try
            {
                if (this.workAction is not null)
                {
                    this.workAction();
                }

                if (this.workTask is not null)
                {
                    this.workTask().Wait();
                }
            }
            finally
            {
                this.currentTask = null;
            }
        });
    }

    private readonly Action? workAction;
    private readonly Func<Task>? workTask;
    private Task? currentTask;
    private Task nextTask;
}
