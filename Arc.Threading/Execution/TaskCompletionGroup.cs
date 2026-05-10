// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Provides an <see cref="ExecutionGroup"/> implementation that completes via an internal <see cref="TaskCompletionSource"/>.
/// </summary>
public class TaskCompletionGroup : ExecutionGroup
{
    private readonly TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the task that is completed when <see cref="TrySetCompleted"/> succeeds.
    /// </summary>
    public Task CompletionTask => this.completionSource.Task;

    // public override bool IsTerminated => !this.CompletionSource.Task.IsCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskCompletionGroup"/> class.
    /// </summary>
    /// <param name="parent">The parent <see cref="ExecutionGroup"/> that contains this group.</param>
    /// <param name="stack">The <see cref="ExecutionStack"/> used for this group's execution context.</param>
    /// <param name="isIndependent">
    /// <see langword="true"/> to make this group independent from the parent group's lifecycle; otherwise, <see langword="false"/>.
    /// </param>
    /// <param name="executionSignalHandler">An optional <see cref="ExecutionSignalHandler"/> that handles execution signals for this group.
    /// </param>
    public TaskCompletionGroup(ExecutionGroup parent, ExecutionStack stack, bool isIndependent = false, ExecutionSignalHandler? executionSignalHandler = default)
        : base(parent, stack, isIndependent, executionSignalHandler)
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
