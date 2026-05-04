// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Support class for <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public class TaskCore2 : ExecutionCore
{
    /// <inheritdoc/>
    public override bool IsTerminated => this.Task.IsCompleted;

    /// <summary>
    /// Gets an instance of <see cref="System.Threading.Tasks.Task"/>.
    /// </summary>
    public Task Task { get; }

    private int started;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore2"/> class.<br/>
    /// method: async <see cref="System.Threading.Tasks.Task"/> Method(<see cref="object"/>? parameter).
    /// </summary>
    /// <param name="parent">The parent of this thread/task.<br/>
    /// Specify <see langword="null"/> to be independent (does not receive a termination signal from parent).</param>
    /// <param name="method">The method that executes on a <see cref="System.Threading.Tasks.Task"/>.</param>
    /// <param name="startImmediately">Starts the task immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="Start"/> to start the task.</param>
    public TaskCore2(ExecutionCore parent, Func<object?, Task> method, bool startImmediately = true)
        : base(parent)
    {
        // this.Task = System.Threading.Tasks.Task.Run(async () => { await method(this); });
        // this.Task = System.Threading.Tasks.Task.Factory.StartNew(async () => { await method(this); }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        // this.Task = new Task(() => method(this).Wait(this.CancellationToken), TaskCreationOptions.LongRunning);

        this.Task = new Task(() => method(this).GetAwaiter().GetResult(), TaskCreationOptions.LongRunning);
        // this.Task = new Task(() => method(this).Wait(), TaskCreationOptions.LongRunning);
        // this.Task = new Task(async () => await method(this).ConfigureAwait(false), TaskCreationOptions.LongRunning);

        if (startImmediately)
        {
            this.SendSignal(ExecutionSignal.Start);
        }
    }

    public override void OnSignalReceived(ExecutionSignal signal)
    {
        if (signal == ExecutionSignal.Start)
        {
            if (Interlocked.CompareExchange(ref this.started, 1, 0) == 0)
            {
                this.Task.Start();
            }

            foreach (var x in this.GetChildren())
            {
                x.SendSignal(signal);
            }
        }

        base.OnSignalReceived(signal);
    }
}
