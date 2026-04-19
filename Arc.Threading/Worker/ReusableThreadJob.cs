// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Arc.Threading;

public record class ReusableThreadJob : ReusableJobBase
{
    private readonly ManualResetEventSlim eventSlim;

    public ReusableThreadJob()
    {
        this.eventSlim = new(false);
    }

    /// <summary>
    /// Blocks the calling thread until this job is signaled as completed.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the wait operation.
    /// </param>
    public void Wait(CancellationToken cancellationToken = default)
    {
        if (this.FireAndForget)
        {
            ThrowFireAndForgetException();
        }

        this.eventSlim.Wait(cancellationToken);
    }

    /// <summary>
    /// Blocks the calling thread until this job is signaled as completed, the timeout expires,
    /// or the wait is canceled.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the job to be signaled.</param>
    /// <param name="cancellationToken">A token used to cancel the wait operation.</param>
    public void Wait(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.FireAndForget)
        {
            ThrowFireAndForgetException();
        }

        this.eventSlim.Wait(timeout, cancellationToken);
    }

    internal override void InitializeInternal()
    {
        if (!this.FireAndForget)
        {
        }
    }

    internal override void SetInternal()
    {
        this.eventSlim.Set();
    }

    internal override void ResetInternal()
    {
        this.eventSlim.Reset();
    }
}

/*public record class ReusableThreadJob : ReusableJobBase
{
    private readonly ManualResetEventSlim eventSlim;

    public ReusableThreadJob()
    {
        this.eventSlim = new(false);
    }

    /// <summary>
    /// Blocks the calling thread until this job is signaled as completed.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the wait operation.
    /// </param>
    public void Wait(CancellationToken cancellationToken = default)
    {
        this.eventSlim.Wait(cancellationToken);
    }

    /// <summary>
    /// Blocks the calling thread until this job is signaled as completed, the timeout expires,
    /// or the wait is canceled.
    /// </summary>
    /// <param name="timeout">
    /// The maximum time to wait for the job to be signaled.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the wait operation.
    /// </param>
    public void Wait(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        this.eventSlim.Wait(timeout, cancellationToken);
    }

    internal override void SetInternal()
    {
        this.eventSlim.Set();
    }

    internal override void ResetInternal()
    {
        this.eventSlim.Reset();
    }
}*/
