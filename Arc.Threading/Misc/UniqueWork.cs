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
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    public UniqueWork(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        this.workAction = action;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UniqueWork"/> class.
    /// </summary>
    /// <param name="asyncAction">The asynchronous work to execute.</param>
    /// <exception cref="ArgumentNullException"><paramref name="asyncAction"/> is <see langword="null"/>.</exception>
    public UniqueWork(Func<Task> asyncAction)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);
        this.workTask = asyncAction;
    }

    /// <summary>
    /// Schedules the work on the thread pool, or joins the work already in progress.
    /// </summary>
    /// <returns>A task that completes when the work completes. Callers that join the work share the same task.</returns>
    /// <remarks>The work never runs inline in this method. Once the work has finished, the next call starts a new run.</remarks>
    public Task Run()
    {
        using (this.syncObject.EnterScope())
        {
            if (this.currentTask is not null)
            {// The work is already in progress.
                return this.currentTask;
            }

            // Publish while holding the lock so a fast completion cannot clear an unpublished task.
            return this.currentTask = this.RunAsync();
        }
    }

    private async Task RunAsync()
    {
        // Continue on the thread pool before running the work, so it never runs inline under the lock (as with Task.Run).
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
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
    private Task? currentTask;
}
