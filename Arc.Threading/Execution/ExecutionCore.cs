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
/// to request termination. Termination is cooperative: running work must observe <see cref="CanContinue"/>
/// or <see cref="CancellationToken"/>.
/// </para>
/// <para>
/// Instances can be attached to an <see cref="ExecutionStack"/>, grouped under an
/// <see cref="ExecutionGroup"/>, and optionally receive application-defined signals via
/// <see cref="SendSignal(ExecutionSignal)"/>.
/// </para>
/// <para>
/// An execution created under or moved to a terminated parent is terminated immediately.
/// A plain <see cref="ExecutionCore"/> (not a derived type) is disposed when termination cancels it.
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
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> or <paramref name="method"/> is <see langword="null"/>.</exception>
    protected static ExecutionGroup ValidateArguments(ExecutionGroup parent, Delegate method)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(method);
        return parent;
    }

    private const int WaitInterval = 10;

    private static int lastId; // Source of default identifiers; 0 is reserved for the root and detached executions.

    #region FieldAndProperty

    private readonly ExecutionSignalHandler? signalHandler;

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
    /// Gets the <see cref="Arc.Threading.ExecutionStack"/> this execution belongs to, or <see langword="null"/> if it belongs to none.
    /// </summary>
    /// <remarks>An execution belongs to at most one stack. Disposal removes it from the stack.</remarks>
    public ExecutionStack? Stack { get; internal set; } // Root.SyncObject

    /// <summary>
    /// Gets or sets the parent execution group.
    /// </summary>
    /// <value>
    /// The parent group, or <see langword="null"/> for the root and for detached or disposed executions.
    /// </value>
    /// <remarks>
    /// Setting this property moves this execution within the same root, under <c>Root.SyncObject</c> synchronization.
    /// The stack membership is not changed.<br/>
    /// The operation rejects invalid relationships such as self-parenting, cycles, and cross-root moves.<br/>
    /// If the new parent is terminated, termination of this execution is requested immediately.<br/>
    /// Setting <see langword="null"/> detaches this execution. Setting the current parent, or the parent of the root, has no effect.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown for self-parenting, cycles, or cross-root moves.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This execution or the new parent has been disposed.</exception>
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
                    ObjectDisposedException.ThrowIf(value.IsDisposed, value);
                    if (this.Root != value.Root)
                    {
                        ExecutionExtensions.ThrowDifferentParentException();
                    }

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
    /// <remarks>
    /// On creation, a nonzero value from a process-wide counter is assigned, so identifiers are unique within the process.
    /// The root uses 0. Disposal resets the identifier to 0.
    /// Uniqueness is not enforced for values set explicitly.
    /// </remarks>
    public int Id { get; set; }

    /// <summary>
    /// Gets a value indicating whether this is the execution root.
    /// </summary>
    public bool IsRoot => ReferenceEquals(this, this.Root); // this.Id == 0;

    /// <summary>
    /// Gets or sets a value indicating whether recursive termination and waiting skip this execution when it is a descendant.
    /// </summary>
    /// <remarks>
    /// An independent descendant and its subtree are skipped unless <see cref="TerminationOptions.IncludeIndependent"/> is specified.
    /// Direct requests to this execution still apply, and groups still forward signals to it.
    /// Parent disposal detaches independent children without canceling them.
    /// </remarks>
    public bool IsIndependent { get; set; }

    /// <summary>
    /// Gets a value indicating whether this execution can continue running, that is, whether termination has not been requested.
    /// </summary>
    public virtual bool CanContinue => !this.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether this execution is terminated.<br/>
    /// The base implementation returns <see langword="true"/> once cancellation has been requested.
    /// </summary>
    /// <remarks>
    /// <see cref="TaskCore"/> and <see cref="ThreadCore"/> override this to report whether their delegate has finished or will never run.
    /// <see cref="WaitForTerminationAsync(TimeSpan, TerminationOptions, CancellationToken)"/> waits for this value.
    /// </remarks>
    public virtual bool IsTerminated => this.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether this instance has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref this.disposed) != 0;

    /// <summary>
    /// Gets the <see cref="System.Threading.CancellationToken"/> associated with this execution.
    /// </summary>
    /// <remarks>
    /// The token is canceled when termination of this execution is requested.
    /// Use <see cref="ExecutionExtensions.AsExecutionCore(System.Threading.CancellationToken)"/> to recover this execution from the token.
    /// Unlike <see cref="CancellationTokenSource.Token"/>, this property does not throw after disposal.
    /// </remarks>
    public CancellationToken CancellationToken => ExecutionExtensions.ToCancellationToken(this);

    /// <summary>
    /// Gets the <see cref="System.Threading.CancellationToken"/> associated with this execution.<br/>
    /// This property hides <see cref="CancellationTokenSource.Token"/> and returns the same token as <see cref="CancellationToken"/>.
    /// </summary>
    /// <remarks>Unlike <see cref="CancellationTokenSource.Token"/>, this property does not throw after disposal.</remarks>
    public new CancellationToken Token => ExecutionExtensions.ToCancellationToken(this);

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionCore"/> class.
    /// </summary>
    /// <param name="parent">The parent group that owns this execution.</param>
    /// <param name="signalHandler">
    /// An optional handler that <see cref="SendSignal(ExecutionSignal)"/> invokes instead of <see cref="OnSignalReceived(ExecutionSignal)"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    /// <remarks>
    /// If <paramref name="parent"/> is terminated, termination of this execution is requested before the constructor returns.
    /// An instance whose exact type is <see cref="ExecutionCore"/> is then also disposed.
    /// </remarks>
    public ExecutionCore(ExecutionGroup parent, ExecutionSignalHandler? signalHandler = default)
        : this(parent, null, false, signalHandler)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionCore"/> class.
    /// </summary>
    /// <param name="parent">The parent group that owns this execution.</param>
    /// <param name="isIndependent">
    /// Whether recursive termination and waiting skip this execution by default. See <see cref="IsIndependent"/>.
    /// </param>
    /// <param name="signalHandler">
    /// An optional handler that <see cref="SendSignal(ExecutionSignal)"/> invokes instead of <see cref="OnSignalReceived(ExecutionSignal)"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    /// <remarks>
    /// If <paramref name="parent"/> is terminated, termination of this execution is requested before the constructor returns.
    /// An instance whose exact type is <see cref="ExecutionCore"/> is then also disposed.
    /// </remarks>
    public ExecutionCore(ExecutionGroup parent, bool isIndependent, ExecutionSignalHandler? signalHandler = default)
        : this(parent, null, isIndependent, signalHandler)
    {
    }

    internal ExecutionCore(ExecutionGroup parent, ExecutionStack? stack, bool isIndependent, ExecutionSignalHandler? signalHandler)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (stack is not null && stack.Root != parent.Root)
        {
            ExecutionExtensions.ThrowDifferentRootException();
        }

        this.Root = parent.Root;
        this.IsIndependent = isIndependent;
        this.signalHandler = signalHandler;
        bool terminateImmediately;
        using (this.Root.SyncObject.EnterScope())
        {
            ObjectDisposedException.ThrowIf(parent.IsDisposed, parent);
            /*while (true)
            {
                var id = Random.Shared.NextInt64();
                if (this.Root.IdToCore.TryAdd(id, this))
                {
                    this.Id = id;
                    break;
                }
            }*/
            int id;
            do
            {
                id = Interlocked.Increment(ref lastId);
            }
            while (id == 0);

            this.Id = id;
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
    /// Asynchronously waits for the specified duration or cancellation.
    /// </summary>
    /// <param name="millisecondsDelay">The number of milliseconds to wait, or <see cref="Timeout.Infinite"/> to wait until canceled.</param>
    /// <param name="cancellationToken">An additional cancellation token that can be used to cancel the delay.</param>
    /// <returns><see langword="true"/> if the delay elapsed; <see langword="false"/> if termination of this execution was requested<br/>
    /// or <paramref name="cancellationToken"/> was canceled before the delay elapsed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="millisecondsDelay"/> is negative other than <see cref="Timeout.Infinite"/>.
    /// </exception>
    public Task<bool> TryDelay(int millisecondsDelay, CancellationToken cancellationToken = default)
        => this.TryDelay(TimeSpan.FromMilliseconds(millisecondsDelay), cancellationToken);

    /// <summary>
    /// Asynchronously waits for the specified duration or cancellation.
    /// </summary>
    /// <param name="delay">The duration to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait until canceled.</param>
    /// <param name="cancellationToken">An additional cancellation token that can be used to cancel the delay.</param>
    /// <returns><see langword="true"/> if the delay elapsed; <see langword="false"/> if termination of this execution was requested<br/>
    /// or <paramref name="cancellationToken"/> was canceled before the delay elapsed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="delay"/> is negative other than <see cref="Timeout.InfiniteTimeSpan"/>, or exceeds the range supported by <see cref="Task.Delay(TimeSpan)"/>.
    /// </exception>
    public async Task<bool> TryDelay(TimeSpan delay, CancellationToken cancellationToken = default)
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

        if (delay == TimeSpan.Zero)
        {
            return true;
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
    /// <param name="options">Options controlling which descendants to wait for.</param>
    /// <param name="cancellationToken">An additional token that can cancel the wait operation.</param>
    /// <returns><see langword="true"/> if termination was observed; <see langword="false"/> if <paramref name="cancellationToken"/> was canceled first.</returns>
    /// <remarks>See <see cref="WaitForTerminationAsync(TimeSpan, TerminationOptions, CancellationToken)"/> for what is waited for.</remarks>
    public Task<bool> WaitForTerminationAsync(TerminationOptions options = default, CancellationToken cancellationToken = default)
        => this.WaitForTerminationAsync(Timeout.InfiniteTimeSpan, options, cancellationToken);

    /// <summary>
    /// Asynchronously waits for the termination of the execution.<br/>
    /// Note that you need to call <see cref="RequestTermination(TerminationOptions)"/> to terminate the execution.
    /// </summary>
    /// <param name="millisecondsTimeout">The maximum number of milliseconds to wait, or <see cref="Timeout.Infinite"/> to wait indefinitely.</param>
    /// <param name="options">Options controlling which descendants to wait for.</param>
    /// <param name="cancellationToken">An additional cancellation token to cancel the wait operation.</param>
    /// <returns><see langword="true"/> if termination was observed; <see langword="false"/> if the timeout elapsed or <paramref name="cancellationToken"/> was canceled first.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="millisecondsTimeout"/> is negative other than <see cref="Timeout.Infinite"/>.
    /// </exception>
    /// <remarks>See <see cref="WaitForTerminationAsync(TimeSpan, TerminationOptions, CancellationToken)"/> for what is waited for.</remarks>
    public Task<bool> WaitForTerminationAsync(int millisecondsTimeout, TerminationOptions options = default, CancellationToken cancellationToken = default)
        => this.WaitForTerminationAsync(TimeSpan.FromMilliseconds(millisecondsTimeout), options, cancellationToken);

    /// <summary>
    /// Asynchronously waits for the termination of the execution.<br/>
    /// Note that you need to call <see cref="RequestTermination(TerminationOptions)"/> to terminate the execution.
    /// </summary>
    /// <param name="timeout">The maximum time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely. <see cref="TimeSpan.Zero"/> checks once without waiting.</param>
    /// <param name="options">Options controlling which descendants to wait for.</param>
    /// <param name="cancellationToken">An additional cancellation token to cancel the wait operation.</param>
    /// <returns><see langword="true"/> if termination was observed; <see langword="false"/> if the timeout elapsed or <paramref name="cancellationToken"/> was canceled first.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeout"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    /// <remarks>
    /// A non-group execution is waited for until <see cref="IsTerminated"/> is <see langword="true"/>.<br/>
    /// Groups are containers: for a group, this waits for its attached non-group descendants, and the group's own state is ignored.
    /// Independent descendants and their subtrees are skipped unless <see cref="TerminationOptions.IncludeIndependent"/> is specified.<br/>
    /// Executions detached from the tree, for example by disposal, are not waited for.
    /// The state is polled at intervals of about 10 milliseconds.
    /// </remarks>
    public virtual async Task<bool> WaitForTerminationAsync(TimeSpan timeout, TerminationOptions options = default, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        PeriodicTimer? timer = null; // Created on the first poll and reused, so each poll does not allocate a delay.
        try
        {
            while (true)
            {
                using (this.Root.SyncObject.EnterScope())
                {
                    if (!HasActiveExecution(this, options))
                    {
                        return true;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
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
                    {// Round up to whole milliseconds: a sub-millisecond period is invalid and a truncated delay would spin.
                        interval = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
                    }
                }

                if (timer is null)
                {
                    timer = new PeriodicTimer(interval);
                }
                else if (timer.Period != interval)
                {
                    timer.Period = interval;
                }

                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            timer?.Dispose();
        }

        static bool HasActiveExecution(ExecutionCore core, TerminationOptions options)
        {
            if (core is ExecutionGroup group)
            {// ExecutionGroup is treated as a container. This method waits for non-group executions only.
                if (group.ChildCount > 0)
                {
                    var children = group.GetChildrenArrayInternal();
                    foreach (var x in children)
                    {// Like RequestTermination(), IsIndependent excludes descendants only, not the target itself.
                        if ((!x.IsIndependent || (options & TerminationOptions.IncludeIndependent) != 0) &&
                            HasActiveExecution(x, options))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            return !core.IsTerminated;
        }
    }

    /// <summary>
    /// Sends a signal to this execution.
    /// </summary>
    /// <param name="signal">The signal payload to dispatch.</param>
    /// <remarks>
    /// If a custom signal handler was provided at construction time, that handler is invoked;
    /// otherwise <see cref="OnSignalReceived(ExecutionSignal)"/> is called. For a group, a custom handler replaces forwarding to its children.<br/>
    /// The signal is processed synchronously on the calling thread, and exceptions thrown while processing it propagate to the caller.
    /// </remarks>
    public void SendSignal(ExecutionSignal signal)
    {
        if (this.signalHandler is null)
        {
            this.OnSignalReceived(signal);
        }
        else
        {
            this.signalHandler.Invoke(this, signal);
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
    /// Requests termination for this execution and its descendants.
    /// </summary>
    /// <param name="options">
    /// Options selecting which descendants are canceled. By default, independent descendants and their subtrees are skipped.
    /// </param>
    /// <remarks>
    /// This execution is canceled even if it is independent. Child executions are canceled recursively unless independent.
    /// Executions attached during termination are also canceled. Calling this method again cancels only executions that are not yet canceled; it has no effect after disposal.<br/>
    /// Cancellation callbacks run synchronously on the calling thread. Their exceptions are ignored so that termination can continue.
    /// An instance whose exact type is <see cref="ExecutionCore"/> is disposed right after it is canceled.<br/>
    /// This method does not wait for running work; use <see cref="WaitForTerminationAsync(TimeSpan, TerminationOptions, CancellationToken)"/>.
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
    /// <returns>A display string containing the name and the lower 16 bits of the identifier in hexadecimal.</returns>
    public override string ToString()
    {
        return $"Core {this.Name} {(ushort)this.Id:x4}"; // Display only the lower 16 bits to keep DebuggerDisplay compact.
    }

    /// <summary>
    /// Releases the resources used by this execution.<br/>
    /// This execution is removed from its parent and stack, and termination is requested.
    /// Non-independent descendants are canceled, then detached from their parents and stacks; independent children are detached without cancellation.
    /// Running threads and tasks are not joined.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
    /// <remarks>
    /// Detached descendants are not disposed, except instances whose exact type is <see cref="ExecutionCore"/>.
    /// Task and thread cores dispose themselves when their delegates exit, unless <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set.<br/>
    /// The identifiers of this execution and its non-independent descendants are reset to 0. Subsequent calls have no effect.
    /// </remarks>
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
        {// The traversal never adds or removes children of the group being iterated, so the list can be iterated without a snapshot.
            foreach (var x in group.GetChildrenSpanInternal())
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
                if (remove && this is not ExecutionGroup)
                {// No descendants: detach before canceling.
                    this.parent?.RemoveChildInternal(this);
                    ProcessCancellationInternal(ref list, this, true, options);
                    remove = false;
                }
                else
                {
                    if (remove)
                    {// Detach this group first, so lookups (GetOrAddGroup, FindChild, ExecutionStack) no longer return it.
                        this.parent?.RemoveChildInternal(this);
                        this.Stack?.RemoveInternal(this);
                    }

                    ProcessCancellationInternal(ref list, this, false, options);
                    if (remove && list.Count == 0)
                    {// Detach descendants only once the subtree is fully canceled, so work attached to a descendant later terminates immediately.
                        ProcessCancellationInternal(ref list, this, true, options); // Detaches only; nothing is left to cancel.
                        remove = false;
                    }
                }
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
