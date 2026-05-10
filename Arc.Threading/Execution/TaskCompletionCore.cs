// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Provides an <see cref="ExecutionCore"/> implementation that completes via an internal <see cref="TaskCompletionSource"/>.
/// </summary>
public class TaskCompletionCore : ExecutionCore
{
    private readonly TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the task that is completed when <see cref="TrySetCompleted"/> succeeds.
    /// </summary>
    public Task CompletionTask => this.completionSource.Task;

    // public override bool IsTerminated => !this.CompletionSource.Task.IsCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCompletionCore"/> class.
    /// </summary>
    /// <param name="parent">The parent execution group that owns this core.</param>
    public TaskCompletionCore(ExecutionGroup parent)
        : base(parent)
    {
    }

    /// <summary>
    /// Attempts to transition <see cref="CompletionTask"/> to the completed state.
    /// </summary>
    /// <remarks>
    /// If the task has already completed, this call has no effect.
    /// </remarks>
    public void TrySetCompleted()
        => this.completionSource.TrySetResult();
}
