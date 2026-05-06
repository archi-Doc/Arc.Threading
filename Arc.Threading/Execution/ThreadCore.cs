// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;

#pragma warning disable SA1124 // Do not use regions

namespace Arc.Threading;

public class ThreadCore : ExecutionCore
{
    private int started;

    public override bool IsTerminated => Volatile.Read(ref this.started) != 0 && !this.Thread.IsAlive; // this.Thread.ThreadState != ThreadState.Running;

    /// <summary>
    /// Gets an instance of <see cref="System.Threading.Thread"/>.
    /// </summary>
    public Thread Thread { get; }

    public bool DisposeOnCompletion { get; init; } = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadCore"/> class.
    /// </summary>
    /// <param name="parent">The parent group that owns this execution.
    /// Specify <see langword="null"/> to be independent (does not receive a termination signal from parent).</param>
    /// <param name="method">The method that executes on a System.Threading.Thread.</param>
    /// <param name="startImmediately">Starts the thread immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ExecutionCore.SendSignal(ExecutionSignal)"/>
    /// with <see cref="ExecutionSignal.Start"/> to start the thread.</param>
    public ThreadCore(ExecutionGroup parent, Action<object?> method, bool startImmediately = true)
        : base(parent)
    {
        ArgumentNullException.ThrowIfNull(method);

        this.Thread = new Thread(new ParameterizedThreadStart(method));
        if (startImmediately)
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
