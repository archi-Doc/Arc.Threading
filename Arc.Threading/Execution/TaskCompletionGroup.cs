// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

public class TaskCompletionGroup : ExecutionGroup
{
    private readonly TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task CompletionTask => this.completionSource.Task;

    // public override bool IsTerminated => !this.CompletionSource.Task.IsCompleted;

    public TaskCompletionGroup(ExecutionGroup parent, ExecutionStack stack, bool isIndependent = false, ExecutionSignalHandler? executionSignalHandler = default)
        : base(parent, stack, isIndependent, executionSignalHandler)
    {
    }

    public void TrySetCompleted()
        => this.completionSource.TrySetResult();
}
