// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents an <see cref="ExecutionCore"/> implementation backed by a dedicated <see cref="System.Threading.Thread"/>.
/// </summary>
/// <remarks>
/// This type starts at most once. When started, it executes the provided delegate and can optionally dispose itself
/// when execution completes.
/// </remarks>
public class ThreadCore : ExecutionCore
{
    private const int StateCreated = 0;
    private const int StateStarted = 1;
    private const int StateAbandoned = 2; // Cancellation won the start race; the thread never starts.

    private readonly Action<ThreadCore> method;
    private int state;

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
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
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
            this.SendSignal(ExecutionSignal.Start);
        }
    }

    /// <summary>
    /// Processes execution signals for this thread core.<br/>
    /// <see cref="ExecutionSignal.Start"/> starts the thread (only once).
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

        var thread = this.Thread;
        if (thread is null)
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
            thread.Start(this);
        }
        catch
        {
            Interlocked.CompareExchange(ref this.state, StateCreated, StateStarted);
            throw;
        }
    }
}
