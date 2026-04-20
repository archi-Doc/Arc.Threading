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
#pragma warning disable SA1405 // Debug.Assert should provide message text

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
/// <see cref="ReusableJobState.Initial"/> -> <see cref="ReusableJobState.Pending"/> ->
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
                Debug.Assert(job.State == ReusableJobState.Pending);
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

                                        job.state = ReusableJobState.Running;
                                        if (worker.processJob is null)
                                        {
                                            await worker.ProcessJob(job).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            worker.processJob(worker, job);
                                        }

                                        job.state = ReusableJobState.Completed;
                                        job.SetInternal();
                                        if (job.FireAndForget)
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
                    job.state = ReusableJobState.Running;

                    if (worker.processJob is null)
                    {
                        await worker.ProcessJob(job).ConfigureAwait(false);
                    }
                    else
                    {
                        worker.processJob(worker, job);
                    }

                    job.state = ReusableJobState.Completed;
                }
                catch
                {
                    job.state = ReusableJobState.Aborted;
                }
                finally
                {
                    job.SetInternal();
                    if (job.FireAndForget)
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
            job.state = ReusableJobState.Aborted;
            job.SetInternal();
            if (job.FireAndForget)
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

    public bool IsCompleted
        => Volatile.Read(ref this.numberOfPendingJobs) == 0 &&
           Volatile.Read(ref this.numberOfActiveTasks) == 0 &&
           this.State == ReusableJobWorkerState.Idle;

    /// <summary>
    /// Gets the current number of jobs waiting to be processed.
    /// </summary>
    public int NumberOfPendingJobs => this.numberOfPendingJobs;

    private readonly ProcessJobDelegate? processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly ConcurrentQueue<TJob> pendingJobs;
    private int numberOfPendingJobs;
    private int numberOfActiveJobs;

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
    /// <param name="fireAndForget">
    /// <see langword="true"/> to execute the job in a fire-and-forget manner without waiting for completion.
    /// </param>
    /// <returns>A job in the <see cref="ReusableJobState.Initial"/> state.</returns>
    public TJob Rent(bool fireAndForget = false)
    {
        var job = this.freeJobs.Rent();
        job.FireAndForget = fireAndForget;
        job.InitializeInternal();
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
        var currentState = job.State;
        if (job.State == ReusableJobState.Completed ||
            job.State == ReusableJobState.Aborted)
        {
            job.state = ReusableJobState.Initial;
            // if (Interlocked.CompareExchange(ref job.state, ReusableJobState.Initial, currentState) == currentState)
            {// Completed->Pooled or Aborted->Pooled
                job.FireAndForget = false;
                job.ResetInternal();
                job.Reset();
                this.freeJobs.Return(job);
            }
        }
    }

    /// <summary>
    /// Enqueues a created job for background processing.
    /// </summary>
    /// <param name="job">The job to enqueue.</param>
    /// <remarks>
    /// Jobs not in the <see cref="ReusableJobState.Initial"/> state are ignored.
    /// </remarks>
    public void Add(TJob job)
    {
        if (job.State != ReusableJobState.Initial)
        {
            return;
        }

        job.state = ReusableJobState.Pending;
        Interlocked.Increment(ref this.numberOfPendingJobs);
        this.pendingJobs.Enqueue(job);
        this.updateEvent?.Pulse();

        if (this.State == ReusableJobWorkerState.Terminated)
        {
            job.state = ReusableJobState.Aborted;
            job.SetInternal();
            if (job.FireAndForget)
            {
                this.Return(job);
            }
        }
    }

    /// <summary>
    /// Waits indefinitely for all pending and active jobs to complete.
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous wait operation. The result is <see langword="true"/> if all jobs completed successfully,<br/>
    /// or <see langword="false"/> if the operation was cancelled.
    /// </returns>
    public Task<bool> WaitForCompletion(CancellationToken cancellationToken = default)
        => this.WaitForCompletion(Timeout.Infinite, cancellationToken);

    /// <summary>
    /// Waits for the completion of all jobs.
    /// </summary>
    /// <param name="timeout">The time span to wait.</param>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns><see langword="true"/>: All works are complete.<br/><see langword="false"/>: Timeout or cancelled.</returns>
    public Task<bool> WaitForCompletion(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return this.WaitForCompletion(Timeout.Infinite, cancellationToken);
        }
        else if (timeout < TimeSpan.Zero ||
            timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return this.WaitForCompletion((int)timeout.TotalMilliseconds, cancellationToken);
    }

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
        if (millisecondsTimeout < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
        }
        else if (this.disposed)
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
