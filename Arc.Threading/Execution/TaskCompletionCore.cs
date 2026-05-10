// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

public class TaskCompletionCore : ExecutionCore
{
    private readonly TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task CompletionTask => this.completionSource.Task;

    // public override bool IsTerminated => !this.CompletionSource.Task.IsCompleted;

    public TaskCompletionCore(ExecutionGroup parent)
        : base(parent)
    {
    }

    public void TrySetCompleted()
        => this.completionSource.TrySetResult();
}
