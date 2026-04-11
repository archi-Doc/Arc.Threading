// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Arc.Collections;

namespace Arc.Threading;

#pragma warning disable SA1629 // Documentation text should end with a period

/// <summary>
/// Provides a reusable, pooled job worker that processes <typeparamref name="TJob"/> instances on a background thread.<br/>
/// To process the actual job, either override ProcessJob (recommended, preferred) or provide processJob in the constructor.<br/>
/// <br/>
/// Example: <br/>
/// var job = worker.Rent();<br/>
/// job.Initialize(10);<br/>
/// worker.Add(job);<br/>
/// job.Wait();<br/>
/// worker.Return(job);
/// </summary>
/// <typeparam name="TJob">
/// The reusable job type handled by this worker. The type must inherit from <see cref="ReusableJobBase"/>
/// and expose a public parameterless constructor.
/// </typeparam>
/// <remarks>
/// This worker combines an internal object pool with a pending queue to reduce allocations and support high-throughput scheduling.<br/>
/// Jobs are expected to follow the lifecycle:<br/>
/// <see cref="ReusableJobState.Created"/> -> <see cref="ReusableJobState.Pending"/> ->
/// <see cref="ReusableJobState.Running"/> -> <see cref="ReusableJobState.Completed"/>.
/// </remarks>
public class ReusableJobWorker<TJob> : ThreadCore, IDisposable
    where TJob : ReusableJobBase, new()
{
    private const int DefaultPoolCapacity = 32;
    private const int WaitTimeout = 100;
    private const int EventTimeout = 1_000;

    private static async void Process(object? parameter)
    {
        var worker = (ReusableJobWorker<TJob>)parameter!;
        while (!worker.IsTerminated)
        {
            try
            {
                if (worker.addedEvent?.Wait(EventTimeout, worker.CancellationToken) == true)
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
                                        if (worker.processJob is null)
                                        {
                                            worker.ProcessJob(job);
                                        }
                                        else
                                        {
                                            worker.processJob(job);
                                        }

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
                if (worker.processJob is null)
                {
                    worker.ProcessJob(job);
                }
                else
                {
                    worker.processJob(job);
                }

                job.State = ReusableJobState.Completed;
                job.SetInternal();
            }

            worker.working = false;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of worker tasks allowed to process queued jobs concurrently.
    /// </summary>
    /// <value>
    /// The concurrency limit for background processing. The default value is <c>1</c>.
    /// </value>
    /// <remarks>
    /// Values greater than <c>1</c> allow additional task-based parallel processing when the queue has enough pending jobs.
    /// </remarks>
    public int NumberOfConcurrentTasks { get; set; } = 1;

    /// <summary>
    /// Gets the current number of jobs waiting to be processed.
    /// </summary>
    public int NumberOfPendingJobs => this.numberOfPendingJobs;

    private readonly Action<TJob>? processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly ConcurrentQueue<TJob> pendingJobs;
    private int numberOfPendingJobs;

    private ManualResetEventSlim? addedEvent = new(false);
    private bool working;
    private int numberOfActiveTasks;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJobWorker{TJob}"/> class.
    /// </summary>
    /// <param name="parent">The parent thread core used for lifecycle coordination, or <see langword="default"/>.</param>
    /// <param name="processJob">
    /// Optional delegate used to process each job. If <see langword="null"/>, <see cref="ProcessJob(TJob)"/> is invoked.
    /// </param>
    /// <param name="poolCapacity">Initial capacity of the reusable job object pool.</param>
    /// <param name="startImmediately">
    /// <see langword="true"/> to start the worker thread during construction; otherwise, manual start is required.
    /// </param>
    public ReusableJobWorker(ThreadCoreBase? parent, Action<TJob>? processJob = default, int poolCapacity = DefaultPoolCapacity, bool startImmediately = true)
        : base(parent, Process, startImmediately)
    {
        this.processJob = processJob;
        this.freeJobs = new(() => new TJob(), poolCapacity);
        this.pendingJobs = new();
    }

    /// <summary>
    /// Processes a single job instance.<br/>
    /// This method must be <b>thread-safe</b>.
    /// </summary>
    /// <param name="job">The job to process.</param>
    /// <remarks>
    /// Override this method when no processing delegate is provided to the constructor.
    /// </remarks>
    public virtual void ProcessJob(TJob job)
    {
    }

    /// <summary>
    /// Rents a reusable job instance from the internal pool.
    /// </summary>
    /// <returns>A job in the <see cref="ReusableJobState.Created"/> state.</returns>
    public TJob Rent()
    {
        var job = this.freeJobs.Rent();
        job.State = ReusableJobState.Created;
        return job;
    }

    /// <summary>
    /// Returns a used job to the internal pool.<br/>
    /// Since it will be reused, be sure to reset the job's internal state.
    /// </summary>
    /// <param name="job">The job to return.</param>
    /// <remarks>
    /// Only jobs in the <see cref="ReusableJobState.Completed"/> state are accepted.
    /// </remarks>
    public void Return(TJob job)
    {
        if (job.State != ReusableJobState.Completed)
        {
            return;
        }

        job.ResetInternal();
        this.freeJobs.Return(job);
    }

    /// <summary>
    /// Enqueues a created job for background processing.
    /// </summary>
    /// <param name="job">The job to enqueue.</param>
    /// <remarks>
    /// Jobs not in the <see cref="ReusableJobState.Created"/> state are ignored.
    /// </remarks>
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
                var cancelled = this.CancellationToken.WaitHandle.WaitOne(WaitTimeout);
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
            }

            base.Dispose(disposing);
        }
    }
}
