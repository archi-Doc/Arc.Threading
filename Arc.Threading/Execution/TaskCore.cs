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
/// The type passed to the execution delegate, normally the deriving type itself. This instance must be a <typeparamref name="TSelf"/>.
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
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> or <paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The current instance is not assignable to <typeparamref name="TSelf"/>.
    /// </exception>
    /// <remarks>
    /// If <paramref name="parent"/> is already terminated, the delegate never runs and <see cref="TaskCore.Task"/> is an already-completed task.
    /// The core is terminated but not disposed, because the derived constructor has not run yet; its owner must dispose it.
    /// </remarks>
    public TaskCore(ExecutionGroup parent, Func<TSelf, Task> method, ExecutionCoreOptions options = ExecutionCoreOptions.None)
        : this(parent, method, options, false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCore{TSelf}"/> class with optional deferred startup.
    /// </summary>
    /// <param name="parent">The owning group.</param>
    /// <param name="method">The execution delegate. The current instance is passed as <typeparamref name="TSelf"/>.</param>
    /// <param name="options">The execution options.</param>
    /// <param name="delayStart">
    /// <see langword="true"/> to skip the automatic start in this constructor. The derived type is then responsible for sending
    /// <see cref="ExecutionSignal.Start"/>, typically at the end of its constructor.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> or <paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The current instance is not assignable to <typeparamref name="TSelf"/>. The instance is disposed before the exception is thrown.
    /// </exception>
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
/// <remarks>
/// The dedicated task waits for the asynchronous delegate; asynchronous continuations may run on other threads.<br/>
/// An <see cref="OperationCanceledException"/> that escapes the delegate after termination was requested is treated as a normal exit.<br/>
/// The core disposes itself when the delegate exits unless <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set.
/// </remarks>
public class TaskCore : ExecutionCore
{
    private const int StateCreated = 0;
    private const int StateStarted = 1;
    private const int StateAbandoned = 2; // Cancellation won the start race; the task never starts.
    private const int StateStartFailed = 3; // Task.Start threw and the task is no longer unstarted; it is never started again.

    private int state;
    private bool keepAbandonedCore; // Set while a base-type constructor starts a derived instance, whose constructor has not run yet.
    private Task? task;
    private Delegate? startMethod; // Func<TCore, Task> run by a constructor-created task; cleared once it runs or is abandoned.

    /// <summary>
    /// Gets a value indicating whether the underlying task has completed,
    /// or whether this core was canceled before the task was started.
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
            return state == StateAbandoned || (canceled && state != StateStarted);
        }
    }

    /// <summary>
    /// Gets the task representing execution of the delegate.
    /// </summary>
    /// <remarks>
    /// Do not start this task directly; send <see cref="ExecutionSignal.Start"/>.<br/>
    /// If this core is already terminated when the task is created, this is an already-completed task and the delegate never runs.
    /// If cancellation later prevents a delayed start, this task remains unstarted.
    /// Use <see cref="ExecutionCore.WaitForTerminationAsync(TerminationOptions, CancellationToken)"/> to observe termination.<br/>
    /// The task completes successfully when the delegate exits with an <see cref="OperationCanceledException"/> after termination was requested; other exceptions fault it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The task has not been initialized yet, that is, a derived type has not called <see cref="Initialize(System.Threading.Tasks.Task)"/>.
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
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> or <paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    /// <remarks>
    /// If <paramref name="parent"/> is already terminated, the delegate never runs and <see cref="TaskCore.Task"/> is an already-completed task.
    /// Unless <see cref="ExecutionCoreOptions.DelayedStart"/> or <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set,
    /// an instance of exactly <see cref="TaskCore"/> is then disposed before the constructor returns; an instance of a derived type is not.
    /// </remarks>
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
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    protected TaskCore(ExecutionGroup parent, ExecutionCoreOptions options)
        : base(parent)
    {
        this.Options = options;
    }

    /// <summary>
    /// Starts this task once for <see cref="ExecutionSignal.Start"/>; other signals are ignored.
    /// </summary>
    /// <param name="signal">The received execution signal.</param>
    /// <remarks>
    /// The signal is ignored after disposal and before the task is initialized.<br/>
    /// If this core was canceled before it started, the task is abandoned and, unless <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set, this core is disposed.
    /// A start sent by a base-type constructor does not dispose an instance of a derived type.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The task cannot be started, for example because it was started directly instead of through this signal.
    /// </exception>
    public override void OnSignalReceived(ExecutionSignal signal)
    {
        if (signal != ExecutionSignal.Start || this.IsDisposed)
        {
            return;
        }

        var task = Volatile.Read(ref this.task);
        if (task is null)
        {
            return;
        }

        if (this.IsCancellationRequested)
        {// Canceled before start: the delegate never runs, so its self-dispose never runs either.
            if (Interlocked.CompareExchange(ref this.state, StateAbandoned, StateCreated) == StateCreated)
            {
                this.DisposeUnstarted();
            }

            return;
        }

        if (Interlocked.CompareExchange(ref this.state, StateStarted, StateCreated) != StateCreated)
        {
            return;
        }

        if (this.IsCancellationRequested)
        {// Canceled after the check above; IsTerminated may already have reported this core as terminated.
            Volatile.Write(ref this.state, StateAbandoned);
            this.DisposeUnstarted();
            return;
        }

        try
        {
            task.Start(TaskScheduler.Default);
        }
        catch
        {
            // Task.Start() fails if the task has already been started or completed, or if the scheduler faults it.
            // Restore the state only while the task is still unstarted, so later Start signals do not retry a start that cannot succeed.
            // A core whose start failed is terminated once it is canceled.
            Interlocked.CompareExchange(ref this.state, task.Status == TaskStatus.Created ? StateCreated : StateStartFailed, StateStarted);
            throw;
        }
    }

    /// <summary>
    /// Assigns the underlying task and starts it unless <see cref="Options"/> contains <see cref="ExecutionCoreOptions.DelayedStart"/>.
    /// </summary>
    /// <param name="task">The unstarted task to manage, such as one created by <see cref="CreateLongRunningTask(TaskCore, Func{Task})"/>. It is started by <see cref="ExecutionSignal.Start"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The task has already been initialized.</exception>
    /// <remarks>
    /// If this core is already canceled when the start is attempted, the task is abandoned.
    /// Unless <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set, an instance of exactly <see cref="TaskCore"/> is then disposed;
    /// an instance of a derived type is not, so its owner must dispose it.
    /// </remarks>
    [MemberNotNull(nameof(task))]
    protected void Initialize(Task task)
        => this.Initialize(task, true);

    /// <summary>
    /// Assigns the underlying task and optionally applies automatic startup.
    /// </summary>
    /// <param name="task">The unstarted task to manage. It is started by <see cref="ExecutionSignal.Start"/>.</param>
    /// <param name="startAutomatically">
    /// <see langword="true"/> to send <see cref="ExecutionSignal.Start"/> now unless <see cref="Options"/> contains <see cref="ExecutionCoreOptions.DelayedStart"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The task has already been initialized.</exception>
    /// <remarks>
    /// If this core is already canceled when the start is attempted, the task is abandoned.
    /// Unless <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set, an instance of exactly <see cref="TaskCore"/> is then disposed;
    /// an instance of a derived type is not, so its owner must dispose it.
    /// </remarks>
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
            this.StartFromConstructor(typeof(TaskCore));
        }
    }

    /// <summary>
    /// Creates a long-running task that executes the specified asynchronous delegate synchronously
    /// on the dedicated long-running task body.
    /// </summary>
    /// <param name="core">The execution core that owns the task.</param>
    /// <param name="method">The asynchronous delegate to execute.</param>
    /// <returns>A non-started long-running task, or a completed task if <paramref name="core"/> is already terminated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> or <paramref name="method"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Pass the result to <see cref="Initialize(System.Threading.Tasks.Task)"/>.
    /// When the delegate exits, <paramref name="core"/> is disposed unless its <see cref="Options"/> contains <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/>.
    /// An <see cref="OperationCanceledException"/> that escapes the delegate while <paramref name="core"/> is canceled completes the task successfully.
    /// </remarks>
    protected Task CreateLongRunningTask(TaskCore core, Func<Task> method)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(method);

        if (core.IsTerminated)
        {
            return Task.CompletedTask;
        }

        // Capture the delegate instead of storing it, so a repeated call cannot replace the delegate of an earlier task.
        return new Task(() => Run(core, _ => method()), TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Creates an unstarted task without allocating a separate delegate to bind its core argument.
    /// </summary>
    /// <typeparam name="TCore">The type passed to the execution delegate.</typeparam>
    /// <param name="core">The owning execution core.</param>
    /// <param name="method">The execution delegate.</param>
    /// <returns>An unstarted task, or a completed task if <paramref name="core"/> is already terminated.</returns>
    private protected static Task CreateLongRunningTask<TCore>(TCore core, Func<TCore, Task> method)
        where TCore : TaskCore
    {
        ArgumentNullException.ThrowIfNull(core);

        if (core.IsTerminated)
        {
            return Task.CompletedTask;
        }

        ((TaskCore)core).startMethod = method;
        return new Task(static state => RunStartMethod((TCore)state!), core, TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Sends <see cref="ExecutionSignal.Start"/> on behalf of a constructor of <paramref name="constructingType"/>.
    /// </summary>
    /// <param name="constructingType">The type whose constructor sends the signal.</param>
    /// <remarks>
    /// If the start is skipped because this core is already canceled, an instance of a more derived type is not disposed,
    /// because its constructor has not run yet; its owner disposes it.
    /// </remarks>
    private protected void StartFromConstructor(Type constructingType)
    {
        this.keepAbandonedCore = this.GetType() != constructingType;
        try
        {
            this.SendSignal(ExecutionSignal.Start);
        }
        finally
        {
            this.keepAbandonedCore = false;
        }
    }

    private static void RunStartMethod<TCore>(TCore core)
        where TCore : TaskCore
    {
        var method = (Func<TCore, Task>)((TaskCore)core).startMethod!;
        ((TaskCore)core).startMethod = null;
        Run(core, method);
    }

    private static void Run<TCore>(TCore core, Func<TCore, Task> method)
        where TCore : TaskCore
    {
        try
        {
            method(core).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (core.IsCancellationRequested)
        {// Cooperative termination is a normal exit (no faulted task or UnobservedTaskException).
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
    }

    private void DisposeUnstarted()
    {
        this.startMethod = null; // The delegate never runs.
        if ((this.Options & ExecutionCoreOptions.NoDisposeOnCompletion) == 0 && !this.keepAbandonedCore)
        {// A derived Dispose(bool) must not run before the derived constructor body.
            this.Dispose();
        }
    }
}
