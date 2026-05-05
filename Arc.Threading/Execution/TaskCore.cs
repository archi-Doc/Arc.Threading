// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

public class TaskCore2<TSelf> : TaskCore
    where TSelf : TaskCore2<TSelf>
{
    public TaskCore2(ExecutionGroup parent, Func<TSelf, Task> method, bool startImmediately = true)
        : base(parent)
    {
        var task = new Task(() => method((TSelf)this).GetAwaiter().GetResult(), TaskCreationOptions.LongRunning);
        this.Initialize(task, startImmediately);
    }
}

/// <summary>
/// Support class for <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public class TaskCore : ExecutionCore
{
    private int started;

    public override bool IsTerminated => this.Task.Status != TaskStatus.Running;

    /// <summary>
    /// Gets an instance of <see cref="System.Threading.Tasks.Task"/>.
    /// </summary>
    public Task Task { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore"/> class.<br/>
    /// method: async <see cref="System.Threading.Tasks.Task"/> Method(<see cref="object"/>? parameter).
    /// </summary>
    /// <param name="parent">The parent of this thread/task.<br/>
    /// Specify <see langword="null"/> to be independent (does not receive a termination signal from parent).</param>
    /// <param name="method">The method that executes on a <see cref="System.Threading.Tasks.Task"/>.</param>
    /// <param name="startImmediately">Starts the task immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ExecutionCore.SendSignal(ExecutionSignal)"/> to start the task.</param>
    public TaskCore(ExecutionGroup parent, Func<TaskCore, Task> method, bool startImmediately = true)
        : base(parent)
    {
        // this.Task = System.Threading.Tasks.Task.Run(async () => { await method(this); });
        // this.Task = System.Threading.Tasks.Task.Factory.StartNew(async () => { await method(this); }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        // this.Task = new Task(() => method(this).Wait(this.CancellationToken), TaskCreationOptions.LongRunning);
        // this.Task = new Task(() => method(this).Wait(), TaskCreationOptions.LongRunning);
        // this.Task = new Task(async () => await method(this).ConfigureAwait(false), TaskCreationOptions.LongRunning);

        var task = new Task(() => method(this).GetAwaiter().GetResult(), TaskCreationOptions.LongRunning);
        this.Initialize(task, startImmediately);
    }

    protected TaskCore(ExecutionGroup parent)
        : base(parent)
    {
        this.Task = default!;
    }

    public override void OnSignalReceived(ExecutionSignal signal)
    {
        if (signal == ExecutionSignal.Start)
        {
            if (Interlocked.CompareExchange(ref this.started, 1, 0) == 0)
            {
                this.Task.Start();
            }
        }
    }

    [MemberNotNull(nameof(Task))]
    protected void Initialize(Task task, bool startImmediately)
    {
        this.Task = task;
        if (startImmediately)
        {
            this.SendSignal(ExecutionSignal.Start);
        }
    }
}
