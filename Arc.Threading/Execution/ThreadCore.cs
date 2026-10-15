// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents an <see cref="ExecutionCore"/> implementation backed by a dedicated <see cref="System.Threading.Thread"/>.
/// </summary>
/// <remarks>
/// This type starts at most once. When started, it executes the provided delegate and, unless <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set,
/// disposes itself when the delegate exits.<br/>
/// The thread is a foreground thread and keeps the process alive until the delegate returns. To make it a background thread,
/// create the core with <see cref="ExecutionCoreOptions.DelayedStart"/>, set <see cref="System.Threading.Thread.IsBackground"/> on <see cref="Thread"/>, then send <see cref="ExecutionSignal.Start"/>.<br/>
/// An <see cref="OperationCanceledException"/> that escapes the delegate after termination was requested is treated as a normal exit;
/// any other exception is unhandled and terminates the process.
/// </remarks>
public class ThreadCore : ExecutionCore
{
    private const int StateCreated = 0;
    private const int StateStarted = 1;
    private const int StateAbandoned = 2; // Cancellation won the start race; the thread never starts.

    private readonly Action<ThreadCore> method;
    private int state;
    private bool keepAbandonedCore; // Set while this constructor starts a derived instance, whose constructor has not run yet.

    /// <summary>
    /// Gets a value indicating whether the underlying thread has completed,<br/>
    /// or whether this execution was canceled before the thread was started.
    /// </summary>
    public override bool IsTerminated
    {
        get
        {
            // Read cancellation before the state: a start claimed after this read observes the cancellation and abandons the thread.
            var canceled = this.IsCancellationRequested;
            var state = Volatile.Read(ref this.state);
            return state == StateAbandoned || (canceled && state == StateCreated) ||
                (this.Thread is { } thread && (thread.ThreadState & ThreadState.Stopped) != 0);
        }
    }

    /// <summary>
    /// Gets the dedicated thread instance used to run this core.
    /// </summary>
    /// <remarks>
    /// Do not start this thread directly; send <see cref="ExecutionSignal.Start"/>.
    /// Properties such as <see cref="System.Threading.Thread.IsBackground"/> can be set before the start when using <see cref="ExecutionCoreOptions.DelayedStart"/>.
    /// </remarks>
    public Thread Thread { get; }

    /// <summary>
    /// Gets the behavior flags controlling this thread core.
    /// </summary>
    public ExecutionCoreOptions Options { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadCore"/> class.
    /// </summary>
    /// <param name="parent">The parent execution group that owns this core.</param>
    /// <param name="method">The delegate executed on the dedicated thread.</param>
    /// <param name="options">Behavior flags controlling startup and completion semantics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> or <paramref name="method"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    /// <remarks>
    /// If <paramref name="parent"/> is already terminated, the thread is never started.
    /// Unless <see cref="ExecutionCoreOptions.DelayedStart"/> or <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set,
    /// an instance of exactly <see cref="ThreadCore"/> is then disposed before the constructor returns; an instance of a derived type is not.
    /// </remarks>
    public ThreadCore(ExecutionGroup parent, Action<ThreadCore> method, ExecutionCoreOptions options = default)
        : base(ValidateArguments(parent, method))
    {
        this.method = method;
        this.Options = options;

        this.Thread = new Thread(static state =>
        {
            var core = (ThreadCore)state!;
            try
            {
                core.method(core);
            }
            catch (OperationCanceledException) when (core.IsCancellationRequested)
            {// Cooperative termination is a normal exit, not an unhandled exception.
            }
            finally
            {
                if ((core.Options & ExecutionCoreOptions.NoDisposeOnCompletion) == 0)
                {
                    // Do not join this thread from Dispose().
                    // DisposeOnCompletion calls Dispose from the thread itself.
                    core.Dispose();
                }
            }
        });

        if ((this.Options & ExecutionCoreOptions.DelayedStart) == 0)
        {
            this.keepAbandonedCore = this.GetType() != typeof(ThreadCore);
            try
            {
                this.SendSignal(ExecutionSignal.Start);
            }
            finally
            {
                this.keepAbandonedCore = false;
            }
        }
    }

    /// <summary>
    /// Starts this thread once for <see cref="ExecutionSignal.Start"/>; other signals are ignored.
    /// </summary>
    /// <param name="signal">The received execution signal.</param>
    /// <remarks>
    /// The signal is ignored after disposal.<br/>
    /// If this core was canceled before it started, the thread is abandoned and, unless <see cref="ExecutionCoreOptions.NoDisposeOnCompletion"/> is set, this core is disposed.
    /// A start sent by the constructor does not dispose an instance of a derived type.
    /// </remarks>
    /// <exception cref="ThreadStateException">The thread was started directly instead of through this signal.</exception>
    public override void OnSignalReceived(ExecutionSignal signal)
    {
        if (signal != ExecutionSignal.Start || this.IsDisposed)
        {
            return;
        }

        var thread = this.Thread;
        if (thread is null)
        {// A group broadcast reached this core during base construction.
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
            thread.Start(this);
        }
        catch
        {// A failed Thread.Start leaves the thread unstarted, so a later Start signal may retry.
            Interlocked.CompareExchange(ref this.state, StateCreated, StateStarted);
            throw;
        }
    }

    private void DisposeUnstarted()
    {
        if ((this.Options & ExecutionCoreOptions.NoDisposeOnCompletion) == 0 && !this.keepAbandonedCore)
        {// A derived Dispose(bool) must not run before the derived constructor body.
            this.Dispose();
        }
    }
}
