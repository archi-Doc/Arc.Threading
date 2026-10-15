// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Provides an <see cref="ExecutionCore"/> implementation that completes via an internal <see cref="TaskCompletionSource"/>.
/// </summary>
/// <remarks>
/// Completion and termination are independent: <see cref="SetCompleted"/> does not terminate this instance,
/// and termination or disposal does not complete <see cref="CompletionTask"/>.<br/>
/// Unlike a plain <see cref="ExecutionCore"/>, this core is not disposed automatically when terminated; dispose it to detach it from its parent.
/// </remarks>
public class TaskCompletionCore : ExecutionCore
{
    private static readonly TaskCompletionSource CompletedMarker = new(); // Records completion before the first read; its task is never exposed.

    private TaskCompletionSource? completionSource; // Created on first read, or CompletedMarker if completed before any read.

    /// <summary>
    /// Gets the task that is completed when <see cref="SetCompleted"/> is called.
    /// </summary>
    /// <remarks>The task is created on first read. Continuations registered before completion run asynchronously.</remarks>
    public Task CompletionTask
    {
        get
        {
            var source = Volatile.Read(ref this.completionSource);
            if (source is null || ReferenceEquals(source, CompletedMarker))
            {
                source = this.CreateCompletionSource(source);
            }

            return source.Task;
        }
    }

    // public override bool IsTerminated => !this.CompletionSource.Task.IsCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCompletionCore"/> class.
    /// </summary>
    /// <param name="parent">The parent execution group that owns this core.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="parent"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    /// <remarks>If <paramref name="parent"/> is terminated, this core is terminated before the constructor returns.</remarks>
    public TaskCompletionCore(ExecutionGroup parent)
        : base(parent)
    {
    }

    /// <summary>
    /// Transitions <see cref="CompletionTask"/> to the completed state.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe. If the task has already completed, this call has no effect.
    /// </remarks>
    public void SetCompleted()
    {// Before the first read, only record the completion; the task is created when it is read.
        var source = Interlocked.CompareExchange(ref this.completionSource, CompletedMarker, null);
        if (source is not null && !ReferenceEquals(source, CompletedMarker))
        {
            source.TrySetResult();
        }
    }

    private TaskCompletionSource CreateCompletionSource(TaskCompletionSource? comparand)
    {
        while (true)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (comparand is not null)
            {// Completed before the first read.
                source.SetResult();
            }

            var current = Interlocked.CompareExchange(ref this.completionSource, source, comparand);
            if (ReferenceEquals(current, comparand))
            {
                return source;
            }
            else if (current is not null && !ReferenceEquals(current, CompletedMarker))
            {
                return current;
            }

            comparand = current; // Completed concurrently: retry from the marker.
        }
    }
}
