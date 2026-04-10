// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Collections;

namespace Arc.Threading;

#pragma warning disable SA1214 // Readonly fields should appear before non-readonly fields
#pragma warning disable SA1304 // Non-private readonly fields should begin with upper-case letter
#pragma warning disable SA1401 // Fields should be private

public abstract class ReusableJobBase
{
    public ReusableJobBase()
    {
    }

    internal virtual void ResetInternal()
    {
    }
}

public class ReusableWork
{
    private readonly ManualResetEventSlim eventSlim;

    public ReusableWork()
    {
        this.eventSlim = new(false);
    }

    public void Wait(CancellationToken cancellationToken = default)
    {
        this.eventSlim.Wait(cancellationToken);
    }

    internal void SetInternal()
    {
        this.eventSlim.Set();
    }

    internal void ResetInternal()
    {
        this.eventSlim.Reset();
    }
}

public class ReusableJobWorker<TJob> : TaskCore, IDisposable
    where TJob : ReusableWork, new()
{
    private static async Task Process(object? parameter)
    {
        var worker = (ReusableJobWorker<TJob>)parameter!;
        while (!worker.IsTerminated)
        {
            var updateEvent = worker.updateEvent;
            if (updateEvent == null)
            {// Disposed
                return;
            }

            try
            {
                await updateEvent.WaitAsync(worker.CancellationToken).ConfigureAwait(false); // Add or Finish
            }
            catch
            {
                return;
            }

            while (worker.pendingJobs.TryDequeue(out var job))
            {
                if (worker.IsTerminated)
                {// Terminated
                    return;
                }

                worker.processJob(job);
                job.SetInternal();
            }
        }
    }

    private AsyncPulseEvent? updateEvent = new();
    // private readonly Func<TWork> workFactory;
    private readonly Action<TJob> processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly CircularQueue<TJob> pendingJobs;

    public ReusableJobWorker(int maxPendingWorks, Action<TJob> workProcess, bool startImmediately = true)
        : base(ThreadCore.Root, Process, startImmediately)
    {
        // this.workFactory = workFactory;
        this.processJob = workProcess;
        this.freeJobs = new(() => new TJob(), 32);
        this.pendingJobs = new(maxPendingWorks);
    }

    public TJob Rent()
    {
        return this.freeJobs.Rent();
    }

    public bool Return(TJob work)
    {
        work.ResetInternal();
        this.freeJobs.Return(work);
        return true;
    }

    public void Add(TJob work)
    {
        while (!this.pendingJobs.TryEnqueue(work))
        {
            Thread.Sleep(10);
        }

        this.updateEvent?.Pulse();
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.updateEvent = null;
            }

            base.Dispose(disposing);
        }
    }
}
