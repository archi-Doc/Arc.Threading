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
/// Provides a reusable, pooled job worker that processes <typeparamref name="TJob"/> instances on a background task.<br/>
/// To process the actual job, either override <see cref="OnJobProcessing(TJob, CancellationToken)"/> (recommended) or provide a <see cref="ProcessJobDelegate"/> in the constructor.<br/>
/// <br/>
/// Example: <br/>
/// var job = worker.Rent(); // Rent a job object from the pool.<br/>
/// job.Initialize(10); // Set the job parameters (user-defined).<br/>
/// worker.Add(job); // Enqueue the job.<br/>
/// await job.WaitAsync(); // Wait until the job is complete.<br/>
/// worker.Return(job); // Return the job object to the pool.
/// </summary>
/// <typeparam name="TJob">
/// The reusable job type handled by this worker. The type must inherit from <see cref="ReusableJob"/>
/// and expose a public parameterless constructor.
/// </typeparam>
/// <remarks>
/// This worker combines an internal object pool with a pending queue to reduce allocations and support high-throughput scheduling.<br/>
/// Jobs are expected to follow the lifecycle:<br/>
/// <see cref="ReusableJobState.Initial"/> -> <see cref="ReusableJobState.Pending"/> ->
/// <see cref="ReusableJobState.Running"/> -> <see cref="ReusableJobState.Completed"/>.
/// </remarks>
public class ReusableJobWorker<TJob> : TaskCore<ReusableJobWorker<TJob>>, IDisposable
    where TJob : ReusableJob, new()
{
    private const int DefaultPoolCapacity = 32;
    private const int DelayMilliseconds = 100;

    /// <summary>
    /// Represents the method that processes a job.
    /// </summary>
    /// <param name="worker">The <see cref="ReusableJobWorker{TJob}"/> instance which owns the job.</param>
    /// <param name="job">The job to process.</param>
    public delegate void ProcessJobDelegate(object worker, TJob job);

    private static async Task Process(ReusableJobWorker<TJob> worker)
    {
        while (worker.CanContinue)
        {
            var addEvent = worker.addEvent;
            if (addEvent is null)
            {// Disposed
                goto Terminated;
            }

            try
            {
                if (await addEvent.WaitAsync(worker.CancellationToken).ConfigureAwait(false) != true)
                {
                    goto Terminated;
                }
            }
            catch
            {
                goto Terminated;
            }

            // worker.OnBeforeProcessJob();
            Interlocked.Increment(ref worker.numberOfTasks);
            while (worker.pendingJobs.TryDequeue(out var job))
            {
                Debug.Assert(job.State == ReusableJobState.Pending);
                var numberOfPendingJobs = Interlocked.Decrement(ref worker.numberOfPendingJobs);

                if (worker.MaxConcurrentTasks > 1)
                {
                    worker.TryAddConcurrentTask(numberOfPendingJobs);
                }

                await ProcessJob(worker, job).ConfigureAwait(false);

                if (worker.IsTerminated)
                {// To prevent the job from freezing, complete the acquired job first, then check whether it has been terminated.
                    Interlocked.Decrement(ref worker.numberOfTasks);
                    goto Terminated;
                }
            }

            Interlocked.Decrement(ref worker.numberOfTasks);
            // worker.OnAfterProcessJob();
        }

Terminated:
        worker.AbortAllJobs();
        worker.OnTerminated();
    }

    private static async Task ProcessJob(ReusableJobWorker<TJob> worker, TJob job)
    {
        try
        {
            job.State = ReusableJobState.Running;
            if (worker.processJob is null)
            {
                await worker.OnJobProcessing(job, worker.CancellationToken).ConfigureAwait(false);
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
            worker.OnJobFinished(job);
            job._SetSynchronizationPrimitive();
            if (job.ReturnToPoolOnCompletion)
            {
                worker.Return(job);
            }
        }
    }

    #region FieldAndProperty

    /// <summary>
    /// Gets or sets the maximum number of worker tasks allowed to process queued jobs concurrently.
    /// </summary>
    /// <value>
    /// The concurrency limit for background processing. The default value is <c>1</c>.
    /// </value>
    /// <remarks>
    /// Values greater than <c>1</c> allow additional task-based parallel processing when the queue has enough pending jobs.
    /// </remarks>
    public int MaxConcurrentTasks { get; set; } = 1;

    /// <summary>
    /// Gets a value indicating whether no job is pending and no job is being processed.
    /// </summary>
    public bool IsCompleted
        => Volatile.Read(ref this.numberOfPendingJobs) == 0 &&
           Volatile.Read(ref this.numberOfTasks) == 0;

    /// <summary>
    /// Gets the current number of jobs waiting to be processed.
    /// </summary>
    public int NumberOfPendingJobs => Volatile.Read(ref this.numberOfPendingJobs);

    private readonly ProcessJobDelegate? processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly ConcurrentQueue<TJob> pendingJobs;
    private AsyncPulseEvent? addEvent = new();
    private int numberOfPendingJobs;
    private int numberOfTasks;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJobWorker{TJob}"/> class.
    /// </summary>
    /// <param name="parent">The parent thread core used for lifecycle coordination, or <see langword="default"/>.</param>
    /// <param name="processJob">
    /// Optional delegate used to process each job. If <see langword="null"/>, <see cref="OnJobProcessing(TJob, CancellationToken)"/> is invoked.
    /// </param>
    /// <param name="poolCapacity">Initial capacity of the reusable job object pool.</param>
    /// <param name="options">Behavior flags controlling startup and completion semantics.</param>
    public ReusableJobWorker(ExecutionGroup parent, ProcessJobDelegate? processJob = default, int poolCapacity = DefaultPoolCapacity, ExecutionCoreOptions options = ExecutionCoreOptions.Default)
        : base(parent, Process, options)
    {
        this.processJob = processJob;
        this.freeJobs = new(() => new(), poolCapacity);
        this.pendingJobs = new();
    }

    /// <summary>
    /// Rents a reusable job instance from the internal pool.
    /// </summary>
    /// <param name="flags">Flags that control the behavior of reusable job instances.</param>
    /// <returns>A job in the <see cref="ReusableJobState.Initial"/> state.</returns>
    public TJob Rent(ReusableJobFlags flags = default)
    {
        var job = this.freeJobs.Rent();
        job.State = ReusableJobState.Initial;
        job.Flags = flags;
        job._PrepareSynchronizationPrimitive();
        return job;
    }

    /// <summary>
    /// Returns a used job to the internal pool.<br/>
    /// Since it will be reused, be sure to reset the job's internal state.<br/>
    /// </summary>
    /// <param name="job">The job to return.</param>
    /// <remarks>
    /// Only jobs in the <see cref="ReusableJobState.Completed"/> or <see cref="ReusableJobState.Aborted"/> state are accepted.<br/>
    /// Other jobs are silently ignored.
    /// </remarks>
    public void Return(TJob job)
    {
        var currentState = job.state;
        if (currentState == (byte)ReusableJobState.Completed ||
            currentState == (byte)ReusableJobState.Aborted)
        {// Completed -> Initial, Aborted -> Initial
            if (Interlocked.CompareExchange(ref job.state, (byte)ReusableJobState.Pooled, currentState) == currentState)
            {
                job.Flags = default;
                job._ResetSynchronizationPrimitive();
                // job.OnReturnToPool();
                this.freeJobs.Return(job);
            }
        }
    }

    /// <summary>
    /// Enqueues a created job for background processing.
    /// </summary>
    /// <param name="job">The job to enqueue.</param>
    /// <remarks>
    /// The job transitions from <see cref="ReusableJobState.Initial"/> to <see cref="ReusableJobState.Pending"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="job"/> is not in the <see cref="ReusableJobState.Initial"/> state.
    /// </exception>
    public void Add(TJob job)
    {
        // Initial -> Pending
        if (Interlocked.CompareExchange(ref job.state, (byte)ReusableJobState.Pending, (byte)ReusableJobState.Initial) != (byte)ReusableJobState.Initial)
        {
            throw new InvalidOperationException("A job can be enqueued only when it is in ReusableJobState.Initial");
        }

        Interlocked.Increment(ref this.numberOfPendingJobs);
        this.pendingJobs.Enqueue(job);
        this.addEvent?.Pulse();

        if (!this.CanContinue)
        {
            this.AbortAllJobs();
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
        if (this.IsDisposed)
        {
            // throw new ObjectDisposedException(this.GetType().Name);
            return false;
        }
        else if (millisecondsTimeout < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
        }

        long startTimestamp = 0;
        if (millisecondsTimeout != Timeout.Infinite)
        {
            startTimestamp = Stopwatch.GetTimestamp();
        }

        while (true)
        {
            if (this.IsCompleted)
            {
                return true;
            }
            else if (this.IsDisposed)
            {
                return false;
            }
            else if (this.IsTerminated)
            {
                return false;
            }

            var delayMilliseconds = DelayMilliseconds;
            if (millisecondsTimeout != Timeout.Infinite)
            {
                var remainingMilliseconds = millisecondsTimeout - (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

                if (remainingMilliseconds <= 0)
                {
                    return false;
                }
                else if (delayMilliseconds > remainingMilliseconds)
                {
                    delayMilliseconds = (int)remainingMilliseconds;
                }
            }

            if (await this.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false) == false)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Processes a single job instance on the background thread.
    /// </summary>
    /// <param name="job">The job to process. The job's state will be <see cref="ReusableJobState.Running"/> when this method is called.</param>
    /// <param name="cancellationToken">A cancellation token that signals when the worker is being terminated.</param>
    /// <returns>A task representing the asynchronous job processing operation.</returns>
    /// <remarks>
    /// Override this method to implement custom job processing logic.<br/>
    /// This method is called automatically by the worker when a job is dequeued from the pending queue.<br/>
    /// Alternatively, you can provide a <c>processJob</c> delegate in the constructor instead of overriding this method.
    /// </remarks>
    protected virtual Task OnJobProcessing(TJob job, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when a job has completed or been aborted, before notifying the synchronization primitive.
    /// </summary>
    /// <param name="job">A completed or aborted job.</param>
    protected virtual void OnJobFinished(TJob job)
    {
    }

    /*/// <summary>
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
    }*/

    /// <summary>
    /// Called once when the worker transitions to the terminated state.
    /// </summary>
    protected virtual void OnTerminated()
    {
    }

    /// <summary>
    /// Releases the resources used by this worker, and aborts the pending jobs.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (!this.IsDisposed)
        {
            if (disposing)
            {
                this.addEvent = null;
            }

            base.Dispose(disposing);

            if (disposing)
            {// Release the jobs which will never be processed (e.g. the worker has not been started).
                this.AbortAllJobs();
            }
        }
    }

    private void TryAddConcurrentTask(int numberOfPendingJobs)
    {// Add a task to process the pending jobs concurrently, if the queue is long enough.
        var currentTasks = Volatile.Read(ref this.numberOfTasks);
        if (currentTasks >= this.MaxConcurrentTasks ||
            currentTasks >= numberOfPendingJobs * 2)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref this.numberOfTasks, currentTasks + 1, currentTasks) != currentTasks)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (this.pendingJobs.TryDequeue(out var job))
                {
                    Interlocked.Decrement(ref this.numberOfPendingJobs);
                    await ProcessJob(this, job).ConfigureAwait(false);

                    if (this.IsTerminated)
                    {// To prevent the job from freezing, complete the acquired job first, then check whether it has been terminated.
                        return;
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref this.numberOfTasks);
            }
        });
    }

    private void AbortAllJobs()
    {
        while (this.pendingJobs.TryDequeue(out var job))
        {// Mark pending jobs as Aborted and return control.
            Interlocked.Decrement(ref this.numberOfPendingJobs);
            job.State = ReusableJobState.Aborted;
            this.OnJobFinished(job);
            job._SetSynchronizationPrimitive();
            if (job.ReturnToPoolOnCompletion)
            {
                this.Return(job);
            }
        }
    }
}
