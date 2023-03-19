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
    public UniqueWork(Action action)
    {
        this.workAction = action;
        this.PrepareNextTask();
    }

    public UniqueWork(Func<Task> task)
    {
        this.workTask = task;
        this.PrepareNextTask();
    }

    public Task Run()
    {
        var original = Interlocked.CompareExchange(ref this.currentTask, this.nextTask, null);
        if (original == null)
        {
            this.PrepareNextTask();

            // this.nextTask is prepared.
            // this.currentTask is not null.
            this.currentTask.Start();
        }

        return this.currentTask;
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

    private Action? workAction;
    private Func<Task>? workTask;
    private Task? currentTask;
    private Task nextTask;
}
