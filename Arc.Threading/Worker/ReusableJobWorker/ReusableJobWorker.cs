// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using Arc.Collections;

namespace Arc.Threading;

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

    private readonly Action<TJob> processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly CircularQueue<TJob> pendingJobs;

    // private AsyncPulseEvent? updateEvent = new();
    private ManualResetEventSlim? addedEvent = new(false);

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
        if (job.State != ReusableJobState.Completed)
        {
            return false;
        }

        job.ResetInternal();
        this.freeJobs.Return(job);
        return true;
    }

    public void Add(TJob job)
    {
        if (job.State != ReusableJobState.Created)
        {
            return;
        }

        while (!this.pendingJobs.TryEnqueue(job))
        {//
            Thread.Sleep(10);
        }

        job.State = ReusableJobState.Pending;
        this.addedEvent?.Set();
        // this.updateEvent?.Pulse();
    }

    public bool TryAdd(TJob job)
    {
        if (job.State != ReusableJobState.Created ||
            !this.pendingJobs.TryEnqueue(job))
        {
            return false;
        }

        job.State = ReusableJobState.Pending;
        this.addedEvent?.Set();
        // this.updateEvent?.Pulse();
        return true;
    }

    /// <summary>
    /// Waits for the completion of all jobs.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public bool WaitForCompletion(int millisecondsTimeout)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }

        var end = Stopwatch.GetTimestamp() + (long)(millisecondsTimeout * (double)Stopwatch.Frequency / 1000);
        while (!this.IsTerminated)
        {
            if (this.pendingJobs.Count == 0)
            {// Complete
                return true;
            }
            else if (millisecondsTimeout >= 0 && Stopwatch.GetTimestamp() >= end)
            {// Timeout
                return false;
            }
            else
            {// Wait
                var cancelled = this.CancellationToken.WaitHandle.WaitOne(10);
                if (cancelled)
                {
                    return false;
                }
            }
        }

        return false;
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
