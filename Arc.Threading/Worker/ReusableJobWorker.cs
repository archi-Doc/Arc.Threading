// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Arc.Collections;

namespace Arc.Threading;

#pragma warning disable SA1214 // Readonly fields should appear before non-readonly fields
#pragma warning disable SA1304 // Non-private readonly fields should begin with upper-case letter
#pragma warning disable SA1401 // Fields should be private

public enum ReusableJobState : byte
{
    /// <summary>
    /// Initial state. The job has been created but not yet queued or scheduled.
    /// </summary>
    Created,

    /// <summary>
    /// Waiting state. The job is queued and waiting to be processed.
    /// </summary>
    Pending,

    /// <summary>
    /// Processing state. The job is currently being executed.
    /// </summary>
    Running,

    /// <summary>
    /// Completed state. The job has finished execution.
    /// </summary>
    Completed,
}

public abstract class ReusableJobBase
{
    public ReusableJobState State { get; internal set; }

    public ReusableJobBase()
    {
    }

    internal abstract void SetInternal();

    internal abstract void ResetInternal();
}

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

public class ReusableTaskJob : ReusableJobBase
{
    private readonly AsyncPulseEvent pulseEvent;

    public ReusableTaskJob()
    {
        this.pulseEvent = new();
    }

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

public class ReusableJobWorker<TJob> : ThreadCore, IDisposable
    where TJob : ReusableJobBase, new()
{
    private const int DefaultQueueCapacity = 32;
    private const int MillisecondsTimeout = 1_000;

    private static async void Process(object? parameter)
    {
        var worker = (ReusableJobWorker<TJob>)parameter!;
        while (!worker.IsTerminated)
        {
            try
            {
                if (worker.addedEvent?.Wait(MillisecondsTimeout, worker.CancellationToken) == true)
                {
                    worker.addedEvent?.Reset();
                }
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

                job.State = ReusableJobState.Running;
                worker.processJob(job);
                job.State = ReusableJobState.Completed;
                job.SetInternal();
            }
        }
    }

    /*private static async Task Process(object? parameter)
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

                job.State = ReusableJobState.Running;
                worker.processJob(job);
                job.State = ReusableJobState.Completed;
                job.SetInternal();
            }
        }
    }*/

    // private AsyncPulseEvent? updateEvent = new();
    private ManualResetEventSlim? addedEvent = new(false);
    private readonly Action<TJob> processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly CircularQueue<TJob> pendingJobs;

    public ReusableJobWorker(Action<TJob> processJob, int queueCapacity = DefaultQueueCapacity, bool startImmediately = true)
        : base(ThreadCore.Root, Process, startImmediately)
    {
        this.processJob = processJob;
        this.freeJobs = new(() => new TJob(), queueCapacity);
        this.pendingJobs = new(queueCapacity);
    }

    public TJob Rent()
    {
        var job = this.freeJobs.Rent();
        job.State = ReusableJobState.Created;
        return job;

    }

    public bool Return(TJob job)
    {
        job.ResetInternal();
        this.freeJobs.Return(job);
        return true;
    }

    public void Add(TJob job)
    {
        while (!this.pendingJobs.TryEnqueue(job))
        {
            Thread.Sleep(10);
        }

        job.State = ReusableJobState.Pending;

        this.addedEvent?.Set();
        // this.updateEvent?.Pulse();
    }

    public bool TryAdd(TJob job)
    {
        if (!this.pendingJobs.TryEnqueue(job))
        {
            return false;
        }

        job.State = ReusableJobState.Pending;
        this.addedEvent?.Set();
        // this.updateEvent?.Pulse();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.addedEvent = null;
                // this.updateEvent = null;
            }

            base.Dispose(disposing);
        }
    }
}
