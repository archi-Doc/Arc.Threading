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
    private readonly Action<ThreadCore> method;
    private int started;

    /// <summary>
    /// Gets a value indicating whether the underlying thread has completed,<br/>
    /// or whether this execution was canceled before the thread was started.
    /// </summary>
    public override bool IsTerminated
        => !this.Thread.IsAlive &&
        (Volatile.Read(ref this.started) != 0 || this.IsCancellationRequested);

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
    /// <remarks>
    /// If <see cref="ExecutionCoreOptions.StartImmediately"/> is specified, a start signal is sent during construction.
    /// If <see cref="ExecutionCoreOptions.DisposeOnCompletion"/> is specified, this instance is disposed in a <see langword="finally"/> block.
    /// </remarks>
    public ThreadCore(ExecutionGroup parent, Action<ThreadCore> method, ExecutionCoreOptions options = ExecutionCoreOptions.Default)
        : base(parent)
    {
        ArgumentNullException.ThrowIfNull(method);

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
                if ((core.Options & ExecutionCoreOptions.DisposeOnCompletion) != 0)
                {
                    // Do not join this thread from Dispose().
                    // DisposeOnCompletion calls Dispose from the thread itself.
                    core.Dispose();
                }
            }
        });

        if ((this.Options & ExecutionCoreOptions.StartImmediately) != 0)
        {
            this.SendSignal(ExecutionSignal.Start);
        }
    }

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

        if (Interlocked.CompareExchange(ref this.started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            this.Thread.Start(this);
        }
        catch
        {
            Interlocked.CompareExchange(ref this.started, 0, 1);
            throw;
        }
    }
}
