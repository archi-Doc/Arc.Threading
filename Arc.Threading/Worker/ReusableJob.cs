// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Arc.Threading;

/// <summary>
/// Represents a reusable worker job without a completion wait primitive.
/// Use <see cref="ReusableTaskJob"/> for asynchronous waits or <see cref="ReusableBlockingJob"/> for blocking waits.
/// </summary>
/// <remarks>
/// Initialize custom fields before submission. Do not mutate or return a submitted job until its wait completes.<br/>
/// Jobs use reference equality. A copy made with a <c>with</c> expression is a new job in the <see cref="ReusableJobState.Initial"/> state.
/// </remarks>
public record class ReusableJob
{
    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> indicating that the job has no synchronization primitive.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
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
    /// <remarks>
    /// <see cref="ReusableJobState.Completed"/> is set before the worker's completion hook runs, and can still change to <see cref="ReusableJobState.Aborted"/>.
    /// Read the outcome after the job's wait completes.
    /// </remarks>
    public ReusableJobState State
    {
        get => (ReusableJobState)Volatile.Read(ref this.state);
        internal set => Volatile.Write(ref this.state, (byte)value);
    }

    /// <summary>
    /// Gets the options that control the behavior of this job. They are assigned when the job is rented, and reset when it is returned.
    /// </summary>
    public ReusableJobOptions Options { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether the job object is automatically returned to the pool when it completes or is aborted.
    /// </summary>
    public bool ReturnsToPoolOnCompletion => (this.Options & ReusableJobOptions.ReturnToPoolOnCompletion) != 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJob"/> class.
    /// </summary>
    public ReusableJob()
    {
        this._PrepareSynchronizationPrimitive();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJob"/> class as a copy of another job.<br/>
    /// Used by <c>with</c> expressions: the copy starts in the <see cref="ReusableJobState.Initial"/> state with default options and its own synchronization primitive.
    /// </summary>
    /// <param name="original">The job to copy. Its state, options, and synchronization primitive are not copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="original"/> is <see langword="null"/>.</exception>
    protected ReusableJob(ReusableJob original)
    {
        ArgumentNullException.ThrowIfNull(original);
        this._PrepareSynchronizationPrimitive();
    }

    /// <summary>
    /// Determines whether the specified job is this instance.
    /// </summary>
    /// <param name="other">The job to compare with this instance.</param>
    /// <returns><see langword="true"/> if <paramref name="other"/> is this instance; otherwise, <see langword="false"/>.</returns>
    /// <remarks>Jobs use reference equality, because their state changes while they are processed and pooled.</remarks>
    public virtual bool Equals(ReusableJob? other)
        => ReferenceEquals(this, other);

    /// <summary>
    /// Returns a hash code based on the identity of this instance.
    /// </summary>
    /// <returns>A hash code that does not change while the job is processed or pooled.</returns>
    public override int GetHashCode()
        => RuntimeHelpers.GetHashCode(this);

    /*/// <summary>
    /// Resets the job to its initial state, allowing it to be reused for another execution.<br/>
    /// This method is intended to be overridden by derived classes to reset custom user-defined state.<br/>
    /// The base implementation is empty and does nothing.
    /// </summary>
    public virtual void OnReturnToPool()
    {
    }*/

    /// <summary>
    /// Prepares the synchronization primitive.
    /// </summary>
    internal virtual void _PrepareSynchronizationPrimitive()
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
