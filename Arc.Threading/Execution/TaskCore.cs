// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Provides a strongly typed <see cref="TaskCore"/> implementation that exposes the current instance
/// as <typeparamref name="TSelf"/> to the execution delegate.
/// </summary>
/// <typeparam name="TSelf">
/// The concrete <see cref="TaskCore{TSelf}"/> type used by the execution delegate.
/// </typeparam>
public class TaskCore<TSelf> : TaskCore
    where TSelf : TaskCore<TSelf>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore{TSelf}"/> class.
    /// </summary>
    /// <param name="parent">The parent execution group that owns this core.</param>
    /// <param name="method">
    /// The asynchronous delegate executed by the underlying long-running task.
    /// The current instance is passed as <typeparamref name="TSelf"/>.
    /// </param>
    /// <param name="options">Behavior flags controlling startup and completion semantics.</param>
    public TaskCore(ExecutionGroup parent, Func<TSelf, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.Default)
        : base(parent, options)
    {
        ArgumentNullException.ThrowIfNull(method);
        this.Initialize(this.CreateLongRunningTask(() => method((TSelf)this)));
    }
}

/// <summary>
/// Represents an <see cref="ExecutionCore"/> backed by a dedicated long-running <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public class TaskCore : ExecutionCore
{
    private int started;

    /// <summary>
    /// Gets a value indicating whether the underlying task is no longer in the <see cref="TaskStatus.Running"/> state.
    /// </summary>
    public override bool IsTerminated => Volatile.Read(ref this.started) != 0 && this.Task.IsCompleted; // this.Task.Status != TaskStatus.Running;

    /// <summary>
    /// Gets the underlying task managed by this execution core.
    /// </summary>
    public Task Task { get; private set; }

    /// <summary>
    /// Gets the behavior flags controlling this task core.
    /// </summary>
    public ExecutionCoreOptions Options { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore"/> class.
    /// </summary>
    /// <param name="parent">The parent execution group that owns this core.</param>
    /// <param name="method">The asynchronous delegate executed by the underlying long-running task.</param>
    /// <param name="options">Behavior flags controlling startup and completion semantics.</param>
    /// <remarks>
    /// If <see cref="ExecutionCoreOptions.StartImmediately"/> is specified, a start signal is sent during construction.
    /// </remarks>
    public TaskCore(ExecutionGroup parent, Func<TaskCore, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.Default)
        : base(parent)
    {
        ArgumentNullException.ThrowIfNull(method);

        this.Options = options;

        // this.Task = System.Threading.Tasks.Task.Run(async () => { await method(this); });
        // this.Task = System.Threading.Tasks.Task.Factory.StartNew(async () => { await method(this); }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        // this.Task = new Task(() => method(this).Wait(this.CancellationToken), TaskCreationOptions.LongRunning);
        // this.Task = new Task(() => method(this).Wait(), TaskCreationOptions.LongRunning);
        // this.Task = new Task(async () => await method(this).ConfigureAwait(false), TaskCreationOptions.LongRunning);

        // var task = new Task(() => method(this).GetAwaiter().GetResult(), TaskCreationOptions.LongRunning);
        this.Initialize(this.CreateLongRunningTask(() => method(this)));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore"/> class for derived types
    /// that defer task creation until a later call to <see cref="Initialize(Task)"/>.
    /// </summary>
    /// <param name="parent">The parent execution group that owns this core.</param>
    /// <param name="options">Behavior flags controlling startup and completion semantics.</param>
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
                this.Task.Start(TaskScheduler.Default);
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

    protected Task CreateLongRunningTask(Func<Task> method)
    {
        ArgumentNullException.ThrowIfNull(method);

        return new Task(
            () =>
            {
                try
                {
                    method().GetAwaiter().GetResult();
                }
                finally
                {
                    if ((this.Options & ExecutionCoreOptions.DisposeOnCompletion) != 0)
                    {
                        this.Dispose();
                    }
                }
            },
            TaskCreationOptions.LongRunning);
    }
}
