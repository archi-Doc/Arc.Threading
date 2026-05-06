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
    private int started;

    /// <summary>
    /// Gets a value indicating whether this execution has started and the underlying thread is no longer alive.
    /// </summary>
    public override bool IsTerminated => Volatile.Read(ref this.started) != 0 && !this.Thread.IsAlive; // this.Thread.ThreadState != ThreadState.Running;

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

        this.Options = options;

        // this.Thread = new Thread(new ParameterizedThreadStart(method));
        this.Thread = new Thread(state =>
        {
            var core = (ThreadCore)state!;
            try
            {
                method(core);
            }
            finally
            {
                if (this.Options.HasFlag(ExecutionCoreOptions.DisposeOnCompletion))
                {
                    core.Dispose();
                }
            }
        });

        if (this.Options.HasFlag(ExecutionCoreOptions.StartImmediately))
        {
            this.SendSignal(ExecutionSignal.Start);
        }
    }

    public override void OnSignalReceived(ExecutionSignal signal)
    {
        if (signal == ExecutionSignal.Start)
        {
            if (Interlocked.CompareExchange(ref this.started, 1, 0) == 0)
            {
                this.Thread.Start(this);
            }
        }
    }
}
