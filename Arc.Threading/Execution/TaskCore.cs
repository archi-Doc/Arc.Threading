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
    public TaskCore(ExecutionGroup parent, Func<TSelf, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.None)
        : this(parent, method, options, false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore{TSelf}"/> class with optional deferred startup.
    /// </summary>
    /// <param name="parent">The owning group.</param>
    /// <param name="method">The execution delegate.</param>
    /// <param name="options">The execution options.</param>
    /// <param name="delayStart">Whether the derived constructor must send the start signal.</param>
    protected TaskCore(ExecutionGroup parent, Func<TSelf, Task> method, ExecutionCoreOptions options, bool delayStart)
        : base(ValidateArguments(parent, method), options)
    {
        if (this is not TSelf self)
        {
            this.Dispose();
            throw new InvalidOperationException($"{this.GetType().Name} must use itself as the {nameof(TSelf)} type argument.");
        }

        this.Initialize(CreateLongRunningTask(self, method), !delayStart);
    }
}

/// <summary>
/// Represents an <see cref="ExecutionCore"/> backed by a dedicated long-running <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
/// <remarks>The dedicated task waits for the asynchronous delegate; asynchronous continuations may run on other threads.</remarks>
public class TaskCore : ExecutionCore
{
    private const int StateCreated = 0;
    private const int StateStarted = 1;
    private const int StateAbandoned = 2; // Cancellation won the start race; the task never starts.

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

            // Read cancellation before the state: a start claimed after this read observes the cancellation and abandons the task.
            var canceled = this.IsCancellationRequested;
            var state = Volatile.Read(ref this.state);
            return state == StateAbandoned || (canceled && state == StateCreated);
        }
    }

    /// <summary>
    /// Gets the task representing execution of the delegate.
    /// </summary>
    /// <remarks>If cancellation prevents a delayed start, this task remains unstarted; use <see cref="ExecutionCore.WaitForTerminationAsync(TerminationOptions, CancellationToken)"/> to observe termination.</remarks>
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
    public TaskCore(ExecutionGroup parent, Func<TaskCore, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.None)
        : base(ValidateArguments(parent, method))
    {
        this.Options = options;
        this.Initialize(CreateLongRunningTask(this, method));
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
    /// Starts this task once for <see cref="ExecutionSignal.Start"/>; other signals are ignored.
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

        if (this.IsCancellationRequested)
        {// Canceled after the check above; IsTerminated may already have reported this core as terminated.
            Volatile.Write(ref this.state, StateAbandoned);
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
        => this.Initialize(task, true);

    /// <summary>
    /// Assigns the underlying task and optionally applies automatic startup.
    /// </summary>
    /// <param name="task">The task to manage.</param>
    /// <param name="startAutomatically">Whether to start unless delayed startup was requested.</param>
    [MemberNotNull(nameof(task))]
    protected void Initialize(Task task, bool startAutomatically)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (Interlocked.CompareExchange(ref this.task, task, null) is not null)
        {
            throw new InvalidOperationException("The task has already been initialized.");
        }

        if (startAutomatically && (this.Options & ExecutionCoreOptions.DelayedStart) == 0)
        {
            this.SendSignal(ExecutionSignal.Start);
        }
    }

    /// <summary>
    /// Creates a long-running task that executes the specified asynchronous delegate synchronously
    /// on the dedicated long-running task body.
    /// </summary>
    /// <param name="core">The execution core that owns the task.</param>
    /// <param name="method">The asynchronous delegate to execute.</param>
    /// <returns>A non-started long-running task, or a completed task if <paramref name="core"/> is already terminated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
    protected Task CreateLongRunningTask(TaskCore core, Func<Task> method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return CreateLongRunningTask(core, _ => method());
    }

    /// <summary>
    /// Creates an unstarted task without allocating a separate delegate to bind its core argument.
    /// </summary>
    /// <typeparam name="TCore">The type passed to the execution delegate.</typeparam>
    /// <param name="core">The owning execution core.</param>
    /// <param name="method">The execution delegate.</param>
    /// <returns>An unstarted task, or a completed task if cancellation already prevented startup.</returns>
    private protected static Task CreateLongRunningTask<TCore>(TCore core, Func<TCore, Task> method)
        where TCore : TaskCore
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(method);

        if (core.IsTerminated)
        {
            return Task.CompletedTask;
        }

        return new Task(
            () =>
            {
                try
                {
                    method(core).GetAwaiter().GetResult();
                }
                finally
                {
                    if ((core.Options & ExecutionCoreOptions.NoDisposeOnCompletion) == 0)
                    {
                        // Do not wait for this.Task from Dispose().
                        // Dispose is called from the task itself.
                        core.Dispose();
                    }
                }
            },
            TaskCreationOptions.LongRunning);
    }
}
