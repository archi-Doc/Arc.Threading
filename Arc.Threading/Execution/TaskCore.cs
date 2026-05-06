// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

public class TaskCore<TSelf> : TaskCore
    where TSelf : TaskCore<TSelf>
{
    public TaskCore(ExecutionGroup parent, Func<TSelf, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.Default)
        : base(parent, options)
    {
        var task = new Task(() => method((TSelf)this).GetAwaiter().GetResult(), TaskCreationOptions.LongRunning);
        this.Initialize(task);
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
    /// Gets the behavior flags controlling this thread core.
    /// </summary>
    public ExecutionCoreOptions Options { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore"/> class.
    /// </summary>
    /// <param name="parent">The parent execution group that owns this core.</param>
    /// <param name="method">The delegate executed on the dedicated thread.</param>
    /// <param name="options">Behavior flags controlling startup and completion semantics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// If <see cref="ExecutionCoreOptions.StartImmediately"/> is specified, a start signal is sent during construction.
    /// If <see cref="ExecutionCoreOptions.DisposeOnCompletion"/> is specified, this instance is disposed in a <see langword="finally"/> block.
    /// </remarks>
    public TaskCore(ExecutionGroup parent, Func<TaskCore, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.Default)
        : base(parent)
    {
        this.Options = options;

        // this.Task = System.Threading.Tasks.Task.Run(async () => { await method(this); });
        // this.Task = System.Threading.Tasks.Task.Factory.StartNew(async () => { await method(this); }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        // this.Task = new Task(() => method(this).Wait(this.CancellationToken), TaskCreationOptions.LongRunning);
        // this.Task = new Task(() => method(this).Wait(), TaskCreationOptions.LongRunning);
        // this.Task = new Task(async () => await method(this).ConfigureAwait(false), TaskCreationOptions.LongRunning);

        var task = new Task(() => method(this).GetAwaiter().GetResult(), TaskCreationOptions.LongRunning);
        this.Initialize(task);
    }

    protected TaskCore(ExecutionGroup parent, ExecutionCoreOptions options)
        : base(parent)
    {
        this.Task = default!;
        this.Options = options;
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
    protected void Initialize(Task task)
    {
        this.Task = task;
        if (this.Options.HasFlag(ExecutionCoreOptions.StartImmediately))
        {
            this.SendSignal(ExecutionSignal.Start);
        }
    }
}
