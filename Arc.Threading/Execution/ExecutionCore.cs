// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Arc.Collections;

namespace Arc.Threading;

/// <summary>
/// Represents a cancellable execution unit in the execution tree.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExecutionCore"/> is the base runtime object used by the threading model to represent
/// an active or terminable execution. It participates in a parent/child hierarchy rooted at
/// <see cref="ExecutionRoot"/> and uses <see cref="CancellationTokenSource"/> cancellation semantics
/// to request termination.
/// </para>
/// <para>
/// Instances can be attached to an <see cref="ExecutionStack"/>, grouped under an
/// <see cref="ExecutionGroup"/>, and optionally receive application-defined signals via
/// <see cref="SendSignal(ExecutionSignal)"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString()}")]
public class ExecutionCore : CancellationTokenSource, IDisposable
{
    /// <summary>
    /// Validates an execution delegate before attaching the execution to its parent.
    /// </summary>
    /// <param name="parent">The owning group.</param>
    /// <param name="method">The execution delegate.</param>
    /// <returns>The validated parent group.</returns>
    protected static ExecutionGroup ValidateParent(ExecutionGroup parent, Delegate method)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(method);
        return parent;
    }

    private const int WaitInterval = 10;

    #region FieldAndProperty

    private readonly ExecutionSignalHandler? executionSignalHandler;

#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter
#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1202 // Elements should be ordered by access
    private int disposed;
    internal ExecutionGroup? parent; // Root.SyncObject
#pragma warning restore SA1202 // Elements should be ordered by access
#pragma warning restore SA1401 // Fields should be private
#pragma warning restore SA1307 // Accessible fields should begin with upper-case letter

    /// <summary>
    /// Gets the execution root that owns this execution tree.
    /// </summary>
    public ExecutionRoot Root { get; }

    /// <summary>
    /// Gets or sets a display name for this execution.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets the owning <see cref="Arc.Threading.ExecutionStack"/> instance.
    /// </summary>
    public ExecutionStack? Stack { get; internal set; } // Root.SyncObject

    /// <summary>
    /// Gets or sets the parent execution group.
    /// </summary>
    /// <remarks>
    /// Setting this property updates group membership under <c>Root.SyncObject</c> synchronization.<br/>
    /// The operation rejects invalid relationships such as self-parenting, cycles, and cross-root moves.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown for self-parenting, cycles, or cross-root moves.
    /// </exception>
    /// <exception cref="ObjectDisposedException">A disposed execution is assigned a new parent.</exception>
    public ExecutionGroup? Parent
    {
        get => this.parent;
        set
        {
            var terminateImmediately = false;
            using (this.Root.SyncObject.EnterScope())
            {
                if (this.IsRoot || this.parent == value)
                {
                    return;
                }

                ObjectDisposedException.ThrowIf(this.IsDisposed, this);
                if (value is not null)
                {
                    if (ReferenceEquals(this, value))
                    {
                        throw new InvalidOperationException("An execution cannot be its own parent.");
                    }

                    if (IsAncestorOf(this, value))
                    {
                        throw new InvalidOperationException("An execution cannot be moved under one of its descendants.");
                    }
                }

                if (value is null)
                {
                    this.parent?.RemoveChildInternal(this);
                }
                else
                {
                    if (this.Root != value.Root)
                    {
                        ExecutionHelper.ThrowDifferentParentException();
                    }

                    this.parent?.RemoveChildInternal(this);
                    value.AddChildInternal(this);
                    terminateImmediately = value.IsTerminated;
                }
            }

            if (terminateImmediately)
            {
                this.RequestTermination();
            }
        }
    }

    /// <summary>
    /// Gets or sets the identifier of this execution.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets a value indicating whether this is the execution root.
    /// </summary>
    public bool IsRoot => ReferenceEquals(this, this.Root); // this.Id == 0;

    /// <summary>
    /// Gets or sets a value indicating whether this execution should be excluded from default
    /// recursive termination requests.
    /// </summary>
    public bool IsIndependent { get; set; }

    /// <summary>
    /// Gets a value indicating whether this execution is active and can continue running.
    /// </summary>
    public virtual bool CanContinue => !this.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether this execution has been terminated.
    /// </summary>
    public virtual bool IsTerminated => this.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether this instance has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref this.disposed) != 0;

    /// <summary>
    /// Gets the <see cref="System.Threading.CancellationToken"/> associated with this execution.
    /// </summary>
    public CancellationToken CancellationToken => ExecutionHelper.Pack(this);

    /// <summary>
    /// Gets the <see cref="System.Threading.CancellationToken"/> associated with this execution.<br/>
    /// This property hides <see cref="CancellationTokenSource.Token"/>.
    /// </summary>
    public new CancellationToken Token => ExecutionHelper.Pack(this);

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionCore"/> class.
    /// </summary>
    /// <param name="parent">The parent group that owns this execution.</param>
    /// <param name="executionSignalHandler">
    /// Optional signal callback invoked by <see cref="SendSignal(ExecutionSignal)"/>.
    /// </param>
    public ExecutionCore(ExecutionGroup parent, ExecutionSignalHandler? executionSignalHandler = default)
        : this(parent, null, false, executionSignalHandler)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionCore"/> class.
    /// </summary>
    /// <param name="parent">The parent group that owns this execution.</param>
    /// <param name="isIndependent">
    /// A value indicating whether this execution is independent from default recursive termination.
    /// </param>
    /// <param name="executionSignalHandler">
    /// Optional signal callback invoked by <see cref="SendSignal(ExecutionSignal)"/>.
    /// </param>
    public ExecutionCore(ExecutionGroup parent, bool isIndependent, ExecutionSignalHandler? executionSignalHandler = default)
        : this(parent, null, isIndependent, executionSignalHandler)
    {
    }

    internal ExecutionCore(ExecutionGroup parent, ExecutionStack? stack, bool isIndependent, ExecutionSignalHandler? executionSignalHandler)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (stack is not null && stack.Root != parent.Root)
        {
            ExecutionHelper.ThrowDifferentRootException();
        }

        this.Root = parent.Root;
        this.IsIndependent = isIndependent;
        this.executionSignalHandler = executionSignalHandler;
        bool terminateImmediately;
        using (this.Root.SyncObject.EnterScope())
        {
            /*while (true)
            {
                var id = Random.Shared.NextInt64();
                if (this.Root.IdToCore.TryAdd(id, this))
                {
                    this.Id = id;
                    break;
                }
            }*/
            this.Id = (int)Random.Shared.NextInt64();
            stack?.AddInternal(this);
            parent.AddChildInternal(this);
            terminateImmediately = parent.IsTerminated;
        }

        if (terminateImmediately)
        {
            this.RequestTermination();
        }
    }

    private protected ExecutionCore()
    {// Root
        this.Root = (ExecutionRoot)this;
        this.Name = "Root";
        this.Id = 0;
        // this.Root.IdToCore[0] = this;
    }

    /// <summary>
    /// Wait for the specified time (<see cref="Task.Delay(TimeSpan)"/>).
    /// </summary>
    /// <param name="millisecondsToWait">The number of milliseconds to wait.</param>
    /// <param name="cancellationToken">An additional cancellation token that can be used to cancel the delay.</param>
    /// <returns><see langword="true"/> if the delay elapsed; otherwise, <see langword="false"/><br/>
    /// if this execution was terminated or the additional cancellation token was canceled.</returns>
    public Task<bool> Delay(int millisecondsToWait, CancellationToken cancellationToken = default)
        => this.Delay(TimeSpan.FromMilliseconds(millisecondsToWait), cancellationToken);

    /// <summary>
    /// Wait for the specified time (<see cref="Task.Delay(TimeSpan)"/>).
    /// </summary>
    /// <param name="delay">The TimeSpan to wait.</param>
    /// <param name="cancellationToken">An additional cancellation token that can be used to cancel the delay.</param>
    /// <returns><see langword="true"/> if the delay elapsed; otherwise, <see langword="false"/><br/>
    /// if this execution was terminated or the additional cancellation token was canceled.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="delay"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public async Task<bool> Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        var internalToken = this.CancellationToken;
        if (internalToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            if (!cancellationToken.CanBeCanceled || cancellationToken == internalToken)
            {
                await Task.Delay(delay, internalToken).ConfigureAwait(false);
            }
            else
            {
                using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(internalToken, cancellationToken);
                await Task.Delay(delay, linkedSource.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asynchronously waits for the termination of the execution.<br/>
    /// Note that you need to call <see cref="RequestTermination(TerminationOptions)"/> to terminate the execution.
    /// </summary>
    /// <param name="options">An additional options for controlling termination behavior.</param>
    /// <param name="cancellationToken">An additional token that can cancel the wait operation.</param>
    /// <returns><see langword="true"/> if termination was observed; otherwise, <see langword="false"/>.</returns>
    public Task<bool> WaitForTermination(TerminationOptions options = default, CancellationToken cancellationToken = default)
        => this.WaitForTermination(Timeout.InfiniteTimeSpan, options, cancellationToken);

    /// <summary>
    /// Asynchronously waits for the termination of the execution.<br/>
    /// Note that you need to call <see cref="RequestTermination(TerminationOptions)"/> to terminate the execution.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait before termination, or -1 to wait indefinitely.</param>
    /// <param name="options">An additional options for controlling termination behavior.</param>
    /// <param name="cancellationToken">An additional cancellation token to cancel the wait operation.</param>
    /// <returns><see langword="true"/> if termination was observed; otherwise, <see langword="false"/>.</returns>
    public Task<bool> WaitForTermination(int millisecondsTimeout, TerminationOptions options = default, CancellationToken cancellationToken = default)
        => this.WaitForTermination(TimeSpan.FromMilliseconds(millisecondsTimeout), options, cancellationToken);

    /// <summary>
    /// Asynchronously waits for the termination of the execution.<br/>
    /// Note that you need to call <see cref="RequestTermination(TerminationOptions)"/> to terminate the execution.
    /// </summary>
    /// <param name="timeout">The <see cref="TimeSpan"/> to wait before termination.</param>
    /// <param name="options">An additional options for controlling termination behavior.</param>
    /// <param name="cancellationToken">An additional cancellation token to cancel the wait operation.</param>
    /// <returns><see langword="true"/> if termination was observed before timeout/cancellation; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeout"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public virtual async Task<bool> WaitForTermination(TimeSpan timeout, TerminationOptions options = default, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            var notTerminated = 0;
            using (this.Root.SyncObject.EnterScope())
            {
                CountObjects(this, ref notTerminated, options);
                if (notTerminated == 0)
                {
                    return true;
                }
            }

            var interval = TimeSpan.FromMilliseconds(WaitInterval);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(startTimestamp);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                if (remaining < interval)
                {
                    interval = remaining;
                }
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        static void CountObjects(ExecutionCore core, ref int notTerminated, TerminationOptions options)
        {
            if (core.IsIndependent &&
                (options & TerminationOptions.IncludeIndependent) == 0)
            {
                return;
            }

            if (core is ExecutionGroup group)
            {// ExecutionGroup is treated as a container. This method waits for non-group executions only.
                if (group.Count > 0)
                {
                    var children = group.GetChildrenArrayInternal();
                    foreach (var x in children)
                    {
                        CountObjects(x, ref notTerminated, options);
                    }
                }
            }
            else
            {
                if (!core.IsTerminated)
                {
                    notTerminated++;
                }
            }
        }
    }

    /// <summary>
    /// Sends a signal to this execution.
    /// </summary>
    /// <param name="signal">The signal payload to dispatch.</param>
    /// <remarks>
    /// If a custom signal handler was provided at construction time, that handler is invoked;
    /// otherwise <see cref="OnSignalReceived(ExecutionSignal)"/> is called.
    /// </remarks>
    public void SendSignal(ExecutionSignal signal)
    {
        if (this.executionSignalHandler is null)
        {
            this.OnSignalReceived(signal);
        }
        else
        {
            this.executionSignalHandler.Invoke(this, signal);
        }
    }

    /// <summary>
    /// Called when a signal is delivered and no external signal handler was supplied.
    /// </summary>
    /// <param name="signal">The received signal.</param>
    /// <remarks>
    /// The base implementation does nothing. Override in derived types to react to signals.
    /// </remarks>
    public virtual void OnSignalReceived(ExecutionSignal signal)
    {
    }

    /// <summary>
    /// Requests termination for this execution.
    /// </summary>
    /// <param name="options">
    /// Termination behavior flags controlling how child executions are processed.
    /// </param>
    /// <remarks>
    /// Calling this method is idempotent. Child executions are canceled recursively unless independent.
    /// Cancellation callback exceptions are ignored so that termination can continue.
    /// </remarks>
    public void RequestTermination(TerminationOptions options = default)
    {
        if (this.IsDisposed)
        {
            return;
        }

        this.RequestTerminationCore(false, options);
    }

    /// <summary>
    /// Returns a compact debug string for this execution.
    /// </summary>
    /// <returns>A display string containing type, name, and truncated identifier.</returns>
    public override string ToString()
    {
        return $"Core {this.Name} {(ushort)this.Id:x4}"; // Display only the lower 16 bits to keep DebuggerDisplay compact.
    }

    /// <summary>
    /// Releases the resources used by this execution.<br/>
    /// Termination is requested, and this execution is removed from the execution tree.
    /// Running threads and tasks are not joined. Independent children are detached without cancellation.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        if (disposing)
        {
            this.RequestTerminationCore(remove: true, default);
        }

        base.Dispose(disposing);
    }

    private static bool IsAncestorOf(ExecutionCore ancestor, ExecutionCore core)
    {
        var current = core.parent;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static void ProcessCancellationInternal(ref TemporaryList<ExecutionCore> list, ExecutionCore core, bool remove, TerminationOptions options)
    {
        if (core is ExecutionGroup group)
        {
            var children = group.GetChildrenArrayInternal();
            foreach (var x in children)
            {
                if (!x.IsIndependent ||
                    (options & TerminationOptions.IncludeIndependent) != 0)
                {
                    ProcessCancellationInternal(ref list, x, remove, options);
                }
                else if (remove)
                {
                    x.parent = null;
                }
            }
        }

        if (!core.IsCancellationRequested)
        {
            list.Add(core);
        }

        if (remove)
        {
            // core.Root.IdToCore.Remove(core.Id);
            core.Stack?.RemoveInternal(core); // core.Stack is cleared inside the called method.

            core.Id = 0;
            core.parent = default;
            if (core is ExecutionGroup group2)
            {
                group2.ClearInternal();
            }
        }
    }

    private void RequestTerminationCore(bool remove, TerminationOptions options)
    {
        TemporaryList<ExecutionCore> list = default;
        while (true)
        {
            using (this.Root.SyncObject.EnterScope())
            {
                if (remove)
                {
                    this.parent?.RemoveChildInternal(this);
                }

                ProcessCancellationInternal(ref list, this, remove, options);
                remove = false;
            }

            if (list.Count == 0)
            {
                break;
            }

            foreach (var x in list)
            {
                try
                {
                    ((CancellationTokenSource)x).Cancel();
                }
                catch
                {// Intentionally ignored. Termination must continue.
                }
                finally
                {
                    if (x.GetType() == typeof(ExecutionCore))
                    {// Automatically remove the ExecutionCore after calling Cancel.
                        x.Dispose();
                    }
                }
            }

            list = default;
        }
    }
}
