// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading.Tasks;

namespace Arc.Threading;

public class TaskCompletionCore : ExecutionCore
{
    public TaskCompletionSource CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // public override bool IsTerminated => !this.CompletionSource.Task.IsCompleted;

    public TaskCompletionCore(ExecutionGroup parent)
        : base(parent)
    {
    }

    public void TrySetCompleted()
        => this.CompletionSource.TrySetResult();
}
