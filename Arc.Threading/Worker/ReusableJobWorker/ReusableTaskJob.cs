// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

/// <summary>
/// Represents a reusable job that uses task-based asynchronous waiting.
/// </summary>
public class ReusableTaskJob : ReusableJobBase
{
    private readonly AsyncPulseEvent pulseEvent;

    public ReusableTaskJob()
    {
        this.pulseEvent = new();
    }

    /// <summary>
    /// Asynchronously waits until this job is signaled.
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that completes when the job is signaled.
    /// </returns>
    public Task Wait(CancellationToken cancellationToken = default)
    {
        return this.pulseEvent.WaitAsync(cancellationToken);
    }

    internal override void SetInternal()
    {
        this.pulseEvent.Pulse();
    }

    internal override void ResetInternal()
    {
    }
}
