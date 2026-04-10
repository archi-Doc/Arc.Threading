// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using Arc.Collections;

namespace Arc.Threading;

#pragma warning disable SA1304 // Non-private readonly fields should begin with upper-case letter
#pragma warning disable SA1401 // Fields should be private

public sealed class QueueWorker<TWork> : ThreadWorkerBase
    where TWork : QueueWorker<TWork>.Work
{
    public abstract class Work
    {
        internal readonly ManualResetEventSlim EventSlim = new(false);

        public Work()
        {
        }

        public abstract void Process(TWork work);
    }

    private readonly Func<TWork> workFactory;
    private readonly Action<TWork> workProcess;
    private readonly ObjectPool<TWork> freeQueue;
    private readonly CircularQueue<TWork> pendingQueue;

    public QueueWorker(Func<TWork> workFactory, int maxPendingWorks, Action<TWork> workProcess)
        : base(ThreadCore.Root, )
    {
        this.workFactory = workFactory;
        this.freeQueue = new(workFactory, 32);
        this.pendingQueue = new(maxPendingWorks);
    }

    public TWork Rent()
    {
        return this.freeQueue.Rent();
    }

    public bool Return(TWork work)
    {
        work.EventSlim.Reset();
        this.freeQueue.Return(work);
        return true;
    }

    public void Add(TWork work)
    {
        if (this.pendingQueue.TryEnqueue(work))
        {
        }
    }
}
