// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Arc.Collections;

namespace Arc.Threading;

#pragma warning disable SA1124 // Do not use regions
#pragma warning disable SA1629 // Documentation text should end with a period

/// <summary>
/// Provides a reusable, pooled job worker that processes <typeparamref name="TJob"/> instances on a background thread.<br/>
/// To process the actual job, either override ProcessJob (recommended) or provide processJob in the constructor.<br/>
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
public class ReusableJobWorker<TJob> : TaskCore, IDisposable
    where TJob : ReusableJobBase, new()
{
    private const int DefaultPoolCapacity = 32;
    private const int WaitTimeout = 100;

    public delegate void ProcessJobDelegate(object worker, TJob job);

    private static async Task Process(object? parameter)
    {
        var worker = (ReusableJobWorker<TJob>)parameter!;
        while (!worker.IsTerminated)
        {
            var updateEvent = worker.updateEvent;
            if (updateEvent is null)
            {// Disposed
                goto Terminated;
            }

            try
            {
                await updateEvent.WaitAsync(worker.CancellationToken).ConfigureAwait(false); // Add or Finish
            }
            catch
            {
                goto Terminated;
            }

            worker.OnBeforeProcessJob();
            worker.State = ReusableJobWorkerState.Working;
            while (worker.pendingJobs.TryDequeue(out var job))
            {
                var numberOfPendingJobs = Interlocked.Decrement(ref worker.numberOfPendingJobs);

                if (worker.NumberOfConcurrentTasks > 1)
                {
                    var current = Volatile.Read(ref worker.numberOfActiveTasks);
                    if (current < worker.NumberOfConcurrentTasks &&
                        current < numberOfPendingJobs * 2)
                    {
                        if (Interlocked.CompareExchange(ref worker.numberOfActiveTasks, current + 1, current) == current)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    while (worker.pendingJobs.TryDequeue(out var job))
                                    {
                                        Interlocked.Decrement(ref worker.numberOfPendingJobs);

                                        job.State = ReusableJobState.Running;
                                        if (worker.processJob is null)
                                        {
                                            await worker.ProcessJob(job).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            worker.processJob(worker, job);
                                        }

                                        job.State = ReusableJobState.Completed;
                                        job.SetInternal();
                                        if (job.AutoReturnOnJobCompletion)
                                        {
                                            worker.Return(job);
                                        }

                                        if (worker.IsTerminated)
                                        {// To prevent the job from freezing, complete the acquired job first, then check whether it has been terminated.
                                            return;
                                        }
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

                try
                {
                    job.State = ReusableJobState.Running;

                    if (worker.processJob is null)
                    {
                        await worker.ProcessJob(job).ConfigureAwait(false);
                    }
                    else
                    {
                        worker.processJob(worker, job);
                    }

                    job.State = ReusableJobState.Completed;
                }
                catch
                {
                    job.State = ReusableJobState.Aborted;
                }
                finally
                {
                    job.SetInternal();
                    if (job.AutoReturnOnJobCompletion)
                    {
                        worker.Return(job);
                    }
                }

                if (worker.IsTerminated)
                {// To prevent the job from freezing, complete the acquired job first, then check whether it has been terminated.
                    goto Terminated;
                }
            }

            worker.State = ReusableJobWorkerState.Idle;
            worker.OnAfterProcessJob();
        }

Terminated:
        worker.State = ReusableJobWorkerState.Terminated;
        while (worker.pendingJobs.TryDequeue(out var job))
        {// Mark pending jobs as Aborted and return control.
            Interlocked.Decrement(ref worker.numberOfPendingJobs);
            job.State = ReusableJobState.Aborted;
            job.SetInternal();
            if (job.AutoReturnOnJobCompletion)
            {
                worker.Return(job);
            }
        }

        worker.OnTerminated();
    }

    #region FieldAndProperty

    public ReusableJobWorkerState State { get; private set; }

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

    private readonly ProcessJobDelegate? processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly ConcurrentQueue<TJob> pendingJobs;
    private int numberOfPendingJobs;

    private AsyncPulseEvent? updateEvent = new();
    private int numberOfActiveTasks;

    #endregion

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
    public ReusableJobWorker(ThreadCoreBase? parent, ProcessJobDelegate? processJob = default, int poolCapacity = DefaultPoolCapacity, bool startImmediately = true)
        : base(parent, Process, startImmediately)
    {
        this.processJob = processJob;
        this.freeJobs = new(() => new TJob(), poolCapacity);
        this.pendingJobs = new();
    }

    /// <summary>
    /// Rents a reusable job instance from the internal pool.
    /// </summary>
    /// <param name="autoReturnOnJobCompletion">
    /// <see langword="true"/> to automatically return the job to the pool when it reaches the completed state;<br/>
    /// Enable this when you do not use the job's return value (fire-and-forget pattern).
    /// </param>
    /// <returns>A job in the <see cref="ReusableJobState.Created"/> state.</returns>
    public TJob Rent(bool autoReturnOnJobCompletion = false)
    {
        var job = this.freeJobs.Rent();
        job.AutoReturnOnJobCompletion = autoReturnOnJobCompletion;
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
        if (job.State == ReusableJobState.Completed ||
            job.State == ReusableJobState.Aborted)
        {// Completed or Aborted
            job.State = ReusableJobState.Created;
            job.AutoReturnOnJobCompletion = false;
            job.ResetInternal();
            job.Reset();
            this.freeJobs.Return(job);
        }
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
        this.updateEvent?.Pulse();

        if (this.State == ReusableJobWorkerState.Terminated)
        {
            job.State = ReusableJobState.Aborted;
            job.SetInternal();
            if (job.AutoReturnOnJobCompletion)
            {
                this.Return(job);
            }
        }
    }

    /// <summary>
    /// Waits for the completion of all jobs.
    /// </summary>
    /// <param name="timeout">The time span to wait.</param>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public Task<bool> WaitForCompletion(TimeSpan timeout, CancellationToken cancellationToken = default)
        => this.WaitForCompletion((int)timeout.TotalMilliseconds, cancellationToken);

    /// <summary>
    /// Waits for the completion of all jobs.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public async Task<bool> WaitForCompletion(int millisecondsTimeout, CancellationToken cancellationToken = default)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }

        var end = Stopwatch.GetTimestamp() + (long)(millisecondsTimeout * (double)Stopwatch.Frequency / 1000);
        while (!this.IsTerminated)
        {
            if (this.numberOfPendingJobs == 0 && this.State == ReusableJobWorkerState.Idle)
            {// Complete
                return true;
            }
            else if (this.State == ReusableJobWorkerState.Terminated)
            {// Terminated
                return false;
            }
            else if (millisecondsTimeout >= 0 && Stopwatch.GetTimestamp() >= end)
            {// Timeout
                return false;
            }
            else
            {// Wait
                if (await this.Delay(WaitTimeout, cancellationToken).ConfigureAwait(false) == false)
                {
                    return false;
                }
            }
        }

        return false;
    }

    protected virtual Task ProcessJob(TJob job)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called before the worker begins processing currently pending jobs.
    /// </summary>
    protected virtual void OnBeforeProcessJob()
    {
    }

    /// <summary>
    /// Called after the worker finishes processing the current batch of pending jobs.
    /// </summary>
    protected virtual void OnAfterProcessJob()
    {
    }

    /// <summary>
    /// Called once when the worker transitions to the terminated state.
    /// </summary>
    protected virtual void OnTerminated()
    {
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
