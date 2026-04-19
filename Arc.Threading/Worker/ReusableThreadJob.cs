// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;

namespace Arc.Threading;

public record class ReusableThreadJob : ReusableJobBase
{
    private ManualResetEventSlim? eventSlim;

    public ReusableThreadJob()
    {
    }

    /// <summary>
    /// Blocks the calling thread until this job is signaled as completed.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the wait operation.
    /// </param>
    public void Wait(CancellationToken cancellationToken = default)
    {
        if (this.eventSlim is null)
        {
            ThrowFireAndForgetException();
        }

        this.eventSlim.Wait(cancellationToken);
    }

    /// <summary>
    /// Blocks the calling thread until this job is signaled as completed, a timeout expires, or cancellation is requested.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the job to complete.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the wait operation.</param>
    /// <returns>
    /// <see langword="true"/> if the job was signaled as completed before the timeout elapsed; otherwise, <see langword="false"/>.
    /// </returns>
    public bool Wait(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.eventSlim is null)
        {
            ThrowFireAndForgetException();
        }

        return this.eventSlim.Wait(timeout, cancellationToken);
    }

    internal override void InitializeInternal()
    {
        if (!this.FireAndForget)
        {
            this.eventSlim = new(false);
        }
    }

    internal override void SetInternal()
    {
        this.eventSlim?.Set();
    }

    internal override void ResetInternal()
    {
        this.eventSlim = default;
    }
}
