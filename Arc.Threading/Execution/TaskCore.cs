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
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The current instance is not assignable to <typeparamref name="TSelf"/>.
    /// </exception>
    public TaskCore(ExecutionGroup parent, Func<TSelf, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.Default)
        : base(parent, options)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (this is not TSelf self)
        {
            throw new InvalidOperationException($"{this.GetType().Name} must use itself as the {nameof(TSelf)} type argument.");
        }

        this.Initialize(this.CreateLongRunningTask(() => method(self)));
    }
}

/// <summary>
/// Represents an <see cref="ExecutionCore"/> backed by a dedicated long-running <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public class TaskCore : ExecutionCore
{
    private const int StateCreated = 0;
    private const int StateStarted = 1;

    private int state;
    private Task? task;

    /// <summary>
    /// Gets a value indicating whether the underlying task has completed,
    /// or whether this core was cancelled before the task was started.
    /// </summary>
    public override bool IsTerminated
    {
        get
        {
            var task = Volatile.Read(ref this.task);
            if (task is null)
            {
                return this.IsCancellationRequested;
            }

            if (task.IsCompleted)
            {
                return true;
            }

            return Volatile.Read(ref this.state) == StateCreated && this.IsCancellationRequested;
        }
    }

    /// <summary>
    /// Gets the underlying task managed by this execution core.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The task has not been initialized yet.
    /// </exception>
    public Task Task => Volatile.Read(ref this.task) ?? throw new InvalidOperationException("The task has not been initialized.");

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
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
    public TaskCore(ExecutionGroup parent, Func<TaskCore, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.Default)
        : base(parent)
    {
        ArgumentNullException.ThrowIfNull(method);

        this.Options = options;
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
        this.Options = options;
    }

    /// <summary>
    /// Processes execution signals for this task core.
    /// </summary>
    /// <param name="signal">The received execution signal.</param>
    public override void OnSignalReceived(ExecutionSignal signal)
    {
        if (signal != ExecutionSignal.Start)
        {
            return;
        }

        if (this.IsDisposed || this.IsCancellationRequested)
        {
            return;
        }

        var task = Volatile.Read(ref this.task);
        if (task is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref this.state, StateStarted, StateCreated) != StateCreated)
        {
            return;
        }

        try
        {
            task.Start(TaskScheduler.Default);
        }
        catch
        {
            // Task.Start() can fail if the task has already transitioned to an invalid state.
            // Restore the state so that IsTerminated does not report a permanently-started task.
            Interlocked.CompareExchange(ref this.state, StateCreated, StateStarted);
            throw;
        }
    }

    /// <summary>
    /// Assigns the underlying task and optionally starts it according to <see cref="Options"/>.
    /// </summary>
    /// <param name="task">The task to manage.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The task has already been initialized.</exception>
    [MemberNotNull(nameof(task))]
    protected void Initialize(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (Interlocked.CompareExchange(ref this.task, task, null) is not null)
        {
            throw new InvalidOperationException("The task has already been initialized.");
        }

        if ((this.Options & ExecutionCoreOptions.DelayedStart) == 0)
        {
            this.SendSignal(ExecutionSignal.Start);
        }
    }

    /// <summary>
    /// Creates a long-running task that executes the specified asynchronous delegate synchronously
    /// on the dedicated long-running task body.
    /// </summary>
    /// <param name="method">The asynchronous delegate to execute.</param>
    /// <returns>A non-started long-running task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
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
                    if ((this.Options & ExecutionCoreOptions.KeepAliveOnCompletion) == 0)
                    {
                        // Do not wait for this.Task from Dispose().
                        // DisposeOnCompletion calls Dispose from the task itself.
                        this.Dispose();
                    }
                }
            },
            TaskCreationOptions.LongRunning);
    }
}
