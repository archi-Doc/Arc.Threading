// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;

namespace Arc.Threading;

public class ReusableThreadJob : ReusableJobBase
{
    private readonly ManualResetEventSlim eventSlim;

    public ReusableThreadJob()
    {
        this.eventSlim = new(false);
    }

    public void Wait(CancellationToken cancellationToken = default)
    {
        this.eventSlim.Wait(cancellationToken);
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
