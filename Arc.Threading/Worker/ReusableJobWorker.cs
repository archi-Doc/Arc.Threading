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
/// To process the actual job, either override <see cref="ProcessJobAsync(TJob, CancellationToken)"/> (recommended) or provide a <see cref="JobProcessor"/> in the constructor.<br/>
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
    public delegate void JobProcessor(object worker, TJob job);

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

                if (!worker.CanContinue)
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
        if (Volatile.Read(ref worker.numberOfTasks) != 0)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref worker.concurrentTasksCompletion, completion);
            if (Volatile.Read(ref worker.numberOfTasks) != 0)
            {
                await completion.Task.ConfigureAwait(false);
            }
        }

        worker.OnTerminated();
    }

    private static async Task ProcessJob(ReusableJobWorker<TJob> worker, TJob job)
    {
        try
        {
            if (!worker.CanContinue)
            {
                job.State = ReusableJobState.Aborted;
                return;
            }

            job.State = ReusableJobState.Running;
            if (worker.processJob is null)
            {
                await worker.ProcessJobAsync(job, worker.CancellationToken).ConfigureAwait(false);
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
            worker.FinishJob(job);
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
    /// Values must be positive. Lowering the limit does not interrupt active processors.
    /// </remarks>
    public int MaxConcurrentTasks
    {
        get => Volatile.Read(ref this.maxConcurrentTasks);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            Volatile.Write(ref this.maxConcurrentTasks, value);
        }
    }

    /// <summary>
    /// Gets a value indicating whether no job is pending and no job is being processed.
    /// </summary>
    public bool IsIdle
        => Volatile.Read(ref this.numberOfOutstandingJobs) == 0 &&
           Volatile.Read(ref this.numberOfTasks) == 0;

    /// <summary>
    /// Gets the current number of jobs waiting to be processed.
    /// </summary>
    public int PendingJobCount => Volatile.Read(ref this.numberOfPendingJobs);

    private readonly JobProcessor? processJob;
    private readonly Func<Task> processConcurrentJobs;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly ConcurrentQueue<TJob> pendingJobs;
    private AsyncPulseEvent? addEvent = new();
    private TaskCompletionSource? concurrentTasksCompletion;
    private int numberOfPendingJobs;
    private int numberOfTasks;
    private int numberOfConcurrentTasks;
    private int maxConcurrentTasks = 1;
    private int numberOfOutstandingJobs;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="ReusableJobWorker{TJob}"/> class.
    /// </summary>
    /// <param name="parent">The parent execution group used for lifecycle coordination.</param>
    /// <param name="processJob">
    /// Optional delegate used to process each job. If <see langword="null"/>, <see cref="ProcessJobAsync(TJob, CancellationToken)"/> is invoked.
    /// </param>
    /// <param name="poolCapacity">Initial capacity of the reusable job object pool.</param>
    /// <param name="options">Behavior flags controlling startup and completion semantics.</param>
    public ReusableJobWorker(ExecutionGroup parent, JobProcessor? processJob = default, int poolCapacity = DefaultPoolCapacity, ExecutionCoreOptions options = ExecutionCoreOptions.None)
        : base(parent, Process, options, true)
    {
        this.processJob = processJob;
        this.processConcurrentJobs = this.ProcessConcurrentJobsAsync;
        this.freeJobs = new(() => new(), poolCapacity);
        this.pendingJobs = new();
        if ((options & ExecutionCoreOptions.DelayedStart) == 0)
        {
            this.SendSignal(ExecutionSignal.Start);
        }
    }

    /// <summary>
    /// Rents a reusable job instance from the internal pool.
    /// </summary>
    /// <param name="options">Options that control the behavior of reusable job instances.</param>
    /// <returns>A job in the <see cref="ReusableJobState.Initial"/> state.</returns>
    public TJob Rent(ReusableJobOptions options = default)
    {
        var job = this.freeJobs.Rent();
        job.State = ReusableJobState.Initial;
        job.Options = options;
        job._PrepareSynchronizationPrimitive();
        return job;
    }

    /// <summary>
    /// Returns a used job to the internal pool.<br/>
    /// Reset custom state and finish all waits before returning the job. Do not access it afterward.<br/>
    /// </summary>
    /// <param name="job">The job to return.</param>
    /// <remarks>
    /// Only jobs in the <see cref="ReusableJobState.Completed"/> or <see cref="ReusableJobState.Aborted"/> state are accepted.<br/>
    /// Other jobs are silently ignored.<br/>
    /// Return a job only after its wait has completed: the state becomes final before <see cref="OnJobFinished(TJob)"/> runs and waiters are released,<br/>
    /// so returning a job as soon as <see cref="ReusableJob.State"/> is final lets the worker touch a job that may already be rented again.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.</exception>
    public void Return(TJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var currentState = Volatile.Read(ref job.state);
        if (currentState == (byte)ReusableJobState.Completed ||
            currentState == (byte)ReusableJobState.Aborted)
        {// Completed -> Pooled, Aborted -> Pooled
            if (Interlocked.CompareExchange(ref job.state, (byte)ReusableJobState.Pooled, currentState) == currentState)
            {
                job.Options = default;
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
    /// A stopped or disposed worker aborts the job and releases its waiters.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="job"/> is not in the <see cref="ReusableJobState.Initial"/> state.
    /// </exception>
    public void Add(TJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        // Initial -> Pending
        if (Interlocked.CompareExchange(ref job.state, (byte)ReusableJobState.Pending, (byte)ReusableJobState.Initial) != (byte)ReusableJobState.Initial)
        {
            throw new InvalidOperationException("A job can be enqueued only when it is in ReusableJobState.Initial");
        }

        Interlocked.Increment(ref this.numberOfOutstandingJobs);
        Interlocked.Increment(ref this.numberOfPendingJobs);
        this.pendingJobs.Enqueue(job);
        this.addEvent?.Pulse();

        if (this.MaxConcurrentTasks > 1 && Volatile.Read(ref this.numberOfTasks) != 0)
        {
            this.TryAddConcurrentTask(Volatile.Read(ref this.numberOfPendingJobs));
        }

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
    /// A task that returns <see langword="true"/> once no jobs remain outstanding, including aborted jobs,<br/>
    /// or <see langword="false"/> if the wait is canceled or the worker is disposed or stops with jobs outstanding.
    /// </returns>
    public Task<bool> WaitForCompletionAsync(CancellationToken cancellationToken = default)
        => this.WaitForCompletionAsync(Timeout.Infinite, cancellationToken);

    /// <summary>
    /// Waits for the completion of all jobs.
    /// </summary>
    /// <param name="timeout">The maximum wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns><see langword="true"/> if all jobs finish; otherwise, <see langword="false"/> on timeout, cancellation, or disposal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is negative other than -1 ms, or exceeds <see cref="int.MaxValue"/> milliseconds.</exception>
    public Task<bool> WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return this.WaitForCompletionAsync(Timeout.Infinite, cancellationToken);
        }
        else if (timeout < TimeSpan.Zero ||
            timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return this.WaitForCompletionAsync((int)timeout.TotalMilliseconds, cancellationToken);
    }

    /// <summary>
    /// Waits for the completion of all jobs.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or -1 to wait indefinitely.</param>
    /// <param name="cancellationToken">
    /// A cancellation token that can be used to cancel the wait operation.
    /// </param>
    /// <returns><see langword="true"/> if all jobs finish; otherwise, <see langword="false"/> on timeout, cancellation, or disposal.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is less than -1.</exception>
    public async Task<bool> WaitForCompletionAsync(int millisecondsTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite);
        if (this.IsDisposed)
        {
            // throw new ObjectDisposedException(this.GetType().Name);
            return false;
        }

        long startTimestamp = 0;
        if (millisecondsTimeout != Timeout.Infinite)
        {
            startTimestamp = Stopwatch.GetTimestamp();
        }

        while (true)
        {
            if (this.IsIdle)
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

            if (await this.TryDelay(delayMilliseconds, cancellationToken).ConfigureAwait(false) == false)
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
    /// Exceptions mark the job as <see cref="ReusableJobState.Aborted"/> and do not stop other jobs.
    /// </remarks>
    protected virtual Task ProcessJobAsync(TJob job, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called before notifying waiters. If this hook throws, the job is aborted and waiters are still notified.
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
    /// Called when the worker loop exits, after all active processors finish.
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
    {// Let each processor recruit another until the queue or concurrency limit is exhausted.
        while (true)
        {
            var currentTasks = Volatile.Read(ref this.numberOfConcurrentTasks);
            if (numberOfPendingJobs <= 0 || !this.CanContinue ||
                currentTasks >= this.MaxConcurrentTasks - 1)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref this.numberOfConcurrentTasks, currentTasks + 1, currentTasks) == currentTasks)
            {
                break;
            }
        }

        Interlocked.Increment(ref this.numberOfTasks);
        if (!this.CanContinue)
        {// Add may race with shutdown; reserve the processor before the final cancellation check.
            this.CompleteConcurrentTask();
            return;
        }

        _ = Task.Run(this.processConcurrentJobs);
    }

    private async Task ProcessConcurrentJobsAsync()
    {
        try
        {
            while (this.pendingJobs.TryDequeue(out var job))
            {
                var numberOfPendingJobs = Interlocked.Decrement(ref this.numberOfPendingJobs);
                this.TryAddConcurrentTask(numberOfPendingJobs);
                await ProcessJob(this, job).ConfigureAwait(false);

                if (!this.CanContinue)
                {// To prevent the job from freezing, complete the acquired job first, then check whether it has been terminated.
                    return;
                }
            }
        }
        finally
        {
            this.CompleteConcurrentTask();
        }
    }

    private void CompleteConcurrentTask()
    {
        Interlocked.Decrement(ref this.numberOfConcurrentTasks);

        // A submission may have seen a full worker after this processor found the queue empty.
        // Hand off that work while this processor is still counted for termination.
        if (!this.pendingJobs.IsEmpty)
        {
            this.TryAddConcurrentTask(Volatile.Read(ref this.numberOfPendingJobs));
        }

        if (Interlocked.Decrement(ref this.numberOfTasks) == 0)
        {
            Volatile.Read(ref this.concurrentTasksCompletion)?.TrySetResult();
        }
    }

    private void AbortAllJobs()
    {
        while (this.pendingJobs.TryDequeue(out var job))
        {// Mark pending jobs as Aborted and return control.
            Interlocked.Decrement(ref this.numberOfPendingJobs);
            job.State = ReusableJobState.Aborted;
            this.FinishJob(job);
        }
    }

    private void FinishJob(TJob job)
    {
        var returnToPool = job.ReturnsToPoolOnCompletion;
        try
        {
            this.OnJobFinished(job);
        }
        catch
        {
            job.State = ReusableJobState.Aborted;
        }
        finally
        {
            job._SetSynchronizationPrimitive();
            if (returnToPool)
            {
                this.Return(job);
            }

            Interlocked.Decrement(ref this.numberOfOutstandingJobs);
        }
    }
}
