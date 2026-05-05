// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using Arc.Threading;

#pragma warning disable SA1124 // Do not use regions

namespace Arc.Threading;

public class ThreadCore : ExecutionCore
{
    private int started;

    public override bool IsTerminated => this.Thread.ThreadState != ThreadState.Running; // (this.started != 0) && (this.Thread.ThreadState & ThreadState.Stopped) == 0;

    /// <summary>
    /// Gets an instance of <see cref="System.Threading.Thread"/>.
    /// </summary>
    public Thread Thread { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadCore"/> class.
    /// </summary>
    /// <param name="parent">The parent of this thread/task.<br/>
    /// Specify <see langword="null"/> to be independent (does not receive a termination signal from parent).</param>
    /// <param name="method">The method that executes on a System.Threading.Thread.</param>
    /// <param name="startImmediately">Starts the thread immediately.<br/>
    /// <see langword="false"/>: Manually call <see cref="ExecutionCore.SendSignal(ExecutionSignal)"/> to start the thread.</param>
    public ThreadCore(ExecutionGroup parent, Action<object?> method, bool startImmediately = true)
        : base(parent)
    {
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
