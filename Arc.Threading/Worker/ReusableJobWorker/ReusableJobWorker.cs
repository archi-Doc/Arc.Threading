// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Arc.Collections;

namespace Arc.Threading;

public class ReusableJobWorker<TJob> : ThreadCore, IDisposable
    where TJob : ReusableJobBase, new()
{
    private const int DefaultPoolCapacity = 32;
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

            worker.working = true;
            while (worker.pendingJobs.TryDequeue(out var job))
            {
                var numberOfPendingJobs = Interlocked.Decrement(ref worker.numberOfPendingJobs);
                if (worker.IsTerminated)
                {// Terminated
                    worker.working = false;
                    return;
                }

                if (worker.NumberOfConcurrentTasks > 1)
                {
                    var current = Volatile.Read(ref worker.numberOfActiveTasks);
                    if (current < worker.NumberOfConcurrentTasks &&
                        current < numberOfPendingJobs * 2)
                    {
                        if (Interlocked.CompareExchange(ref worker.numberOfActiveTasks, current + 1, current) == current)
                        {
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    while (worker.pendingJobs.TryDequeue(out var job))
                                    {
                                        Interlocked.Decrement(ref worker.numberOfPendingJobs);

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
                                finally
                                {
                                    Interlocked.Decrement(ref worker.numberOfActiveTasks);
                                }
                            });
                        }
                    }
                }

                job.State = ReusableJobState.Running;
                worker.processJob(job);
                job.State = ReusableJobState.Completed;
                job.SetInternal();
            }

            worker.working = false;
        }
    }

    public int NumberOfConcurrentTasks { get; set; } = 1;

    public int NumberOfPendingJobs => this.numberOfPendingJobs;

    private readonly Action<TJob> processJob;
    private readonly ObjectPool<TJob> freeJobs;
    // private readonly CircularQueue<TJob> pendingJobs;
    private readonly ConcurrentQueue<TJob> pendingJobs;
    private int numberOfPendingJobs;

    private ManualResetEventSlim? addedEvent = new(false);
    private bool working;
    private int numberOfActiveTasks;

    public ReusableJobWorker(ThreadCoreBase? parent, Action<TJob> processJob, int poolCapacity = DefaultPoolCapacity, bool startImmediately = true)
        : base(parent, Process, startImmediately)
    {
        this.processJob = processJob;
        this.freeJobs = new(() => new TJob(), poolCapacity);
        this.pendingJobs = new();
        // this.pendingJobs = new(poolCapacity);
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

        this.pendingJobs.Enqueue(job);
        Interlocked.Increment(ref this.numberOfPendingJobs);

        job.State = ReusableJobState.Pending;
        this.addedEvent?.Set();
        // this.updateEvent?.Pulse();
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
            if (this.numberOfPendingJobs == 0 && !this.working)
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
