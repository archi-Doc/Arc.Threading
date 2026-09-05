// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Runs one operation at a time; overlapping callers share its task.
/// Asynchronous work is awaited without blocking a thread. Failures propagate to all callers.
/// </summary>
public class UniqueWork
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UniqueWork"/> class.
    /// </summary>
    /// <param name="action">The work to execute.</param>
    public UniqueWork(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        this.workAction = action;
        this.runWork = this.RunAsync;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UniqueWork"/> class.
    /// </summary>
    /// <param name="task">The asynchronous work to execute.</param>
    public UniqueWork(Func<Task> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        this.workTask = task;
        this.runWork = this.RunAsync;
    }

    /// <summary>
    /// Starts the work, or joins the work which is already in progress.
    /// </summary>
    /// <returns>The task of the work being executed.</returns>
    public Task Run()
    {
        using (this.syncObject.EnterScope())
        {
            if (this.currentTask is not null)
            {// The work is already in progress.
                return this.currentTask;
            }

            // Publish while holding the lock so a fast completion cannot clear an unpublished task.
            return this.currentTask = Task.Run(this.runWork);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            if (this.workAction is not null)
            {
                this.workAction();
            }

            if (this.workTask is not null)
            {
                await this.workTask().ConfigureAwait(false);
            }
        }
        finally
        {
            using (this.syncObject.EnterScope())
            {
                this.currentTask = null;
            }
        }
    }

    private readonly Action? workAction;
    private readonly Func<Task>? workTask;
    private readonly Lock syncObject = new();
    private readonly Func<Task> runWork;
    private Task? currentTask;
}
