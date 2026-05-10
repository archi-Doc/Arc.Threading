// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

public class TaskCompletionGroup : ExecutionGroup
{
    public TaskCompletionSource CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // public override bool IsTerminated => !this.CompletionSource.Task.IsCompleted;

    public TaskCompletionGroup(ExecutionGroup parent, ExecutionStack stack, bool isIndependent = false, ExecutionSignalHandler? executionSignalHandler = default)
        : base(parent, stack, isIndependent, executionSignalHandler)
    {
    }

    public void TrySetCompleted()
        => this.CompletionSource.TrySetResult();
}
