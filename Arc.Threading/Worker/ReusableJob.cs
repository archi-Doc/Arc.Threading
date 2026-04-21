// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Arc.Threading;

/// <summary>
/// Represents the base record class for reusable jobs that can be executed by a worker.<br/>
/// Since this class does not provide a way to wait for completion, inherit from <br/>
/// <see cref="ReusableTaskJob" /> (TaskCompletionSource-based, recommended) or <see cref="ReusableThreadJob" /> (ManualResetEventSlim-based).
/// </summary>
public record class ReusableJob
{
    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowFireAndForgetException()
        => throw new InvalidOperationException("Since this job uses the fire-and-forget pattern, you cannot wait for it to complete");

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowNoSynchronizationPrimitive()
        => throw new InvalidOperationException("Failed to obtain the job's synchronization primitive");

#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter
    internal byte state;
#pragma warning restore SA1307 // Accessible fields should begin with upper-case letter
#pragma warning restore SA1401 // Fields should be private

    /// <summary>
    /// Gets the current state of the reusable job.
    /// </summary>
    public ReusableJobState State
    {
        get => (ReusableJobState)this.state;
        internal set => this.state = (byte)value;
    }

    public ReusableJobFlags Flags { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether the job object is automatically returned to the pool on completion.
    /// </summary>
    public bool ReturnToPoolOnCompletion => (this.Flags & ReusableJobFlags.ReturnToPoolOnCompletion) != 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJob"/> class.
    /// </summary>
    public ReusableJob()
    {
    }

    /*/// <summary>
    /// Resets the job to its initial state, allowing it to be reused for another execution.<br/>
    /// This method is intended to be overridden by derived classes to reset custom user-defined state.<br/>
    /// The base implementation is empty and does nothing.
    /// </summary>
    public virtual void OnReturnToPool()
    {
    }*/

    /// <summary>
    /// Initializes the synchronization primitive.
    /// </summary>
    internal virtual void _InitializeSynchronizationPrimitive()
    {
    }

    /// <summary>
    /// Sets the synchronization primitive.
    /// </summary>
    internal virtual void _SetSynchronizationPrimitive()
    {
    }

    /// <summary>
    /// Resets the synchronization primitive.
    /// </summary>
    internal virtual void _ResetSynchronizationPrimitive()
    {
    }
}
