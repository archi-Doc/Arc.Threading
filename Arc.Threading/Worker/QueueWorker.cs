// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System.Threading;
using Arc.Collections;

namespace Arc.Threading;

public sealed class QueueWorker<TWork>
    where TWork : QueueWorker<TWork>.Work, new()
{
    public abstract class Work
    {
        private readonly ManualResetEventSlim eventSlim = new(false);

        public Work()
        {
        }
    }

    private readonly ObjectPool<TWork> freeWorks = new(() => new(TWork), 32);
    private readonly CircularQueue<TWork> pendingWorks;


    public QueueWorker(int maxPendingWorks)
    {
        this.pendingWorks = new(maxPendingWorks);
    }

    public TWork Rent()
    {
        return this.freeWorks.Rent();
    }

    public bool Return(TWork work)
    {
        this.freeWorks.Return(work);
        return true;
    }

    public void Add(TWork)
    {
    }
}
