// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
/// job.WaitAsync();<br/>
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
    private const int DelayMilliseconds = 100;

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

            // worker.OnBeforeProcessJob();
            worker.State = ReusableJobWorkerState.Working;
            Interlocked.Increment(ref worker.numberOfTasks);
            while (worker.pendingJobs.TryDequeue(out var job))
            {
                Debug.Assert(job.State == ReusableJobState.Pending);
                var numberOfPendingJobs = Interlocked.Decrement(ref worker.numberOfPendingJobs);

                if (worker.MaxConcurrentTasks > 1)
                {
                    var currentTasks = Volatile.Read(ref worker.numberOfTasks);
                    if (currentTasks < worker.MaxConcurrentTasks &&
                        currentTasks < numberOfPendingJobs * 2)
                    {
                        if (Interlocked.CompareExchange(ref worker.numberOfTasks, currentTasks + 1, currentTasks) == currentTasks)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    while (worker.pendingJobs.TryDequeue(out var job))
                                    {
                                        Interlocked.Decrement(ref worker.numberOfPendingJobs);

                                        // ProcessJobCode
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
                                            job._SetSynchronizationPrimitive();
                                            if (job.FireAndForget)
                                            {
                                                worker.Return(job);
                                            }
                                        }

                                        if (worker.IsTerminated)
                                        {// To prevent the job from freezing, complete the acquired job first, then check whether it has been terminated.
                                            return;
                                        }
                                    }
                                }
                                finally
                                {
                                    Interlocked.Decrement(ref worker.numberOfTasks);
                                }
                            });
                        }
                    }
                }

                // ProcessJobCode
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
                    job._SetSynchronizationPrimitive();
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
            Interlocked.Decrement(ref worker.numberOfTasks);
            // worker.OnAfterProcessJob();
        }

Terminated:
        worker.State = ReusableJobWorkerState.Terminated;
        while (worker.pendingJobs.TryDequeue(out var job))
        {// Mark pending jobs as Aborted and return control.
            Interlocked.Decrement(ref worker.numberOfPendingJobs);
            job.State = ReusableJobState.Aborted;
            job._SetSynchronizationPrimitive();
            if (job.FireAndForget)
            {
                worker.Return(job);
            }
        }

        worker.OnTerminated();
    }

    #region FieldAndProperty

    public ReusableJobWorkerState State
    {
        get => (ReusableJobWorkerState)Volatile.Read(ref this.state);
        private set => Volatile.Write(ref this.state, (int)value);
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
    public int MaxConcurrentTasks { get; set; } = 1;

    public bool IsCompleted
        => Volatile.Read(ref this.numberOfPendingJobs) == 0 &&
           Volatile.Read(ref this.numberOfTasks) == 0 &&
           this.State == ReusableJobWorkerState.Idle;

    /// <summary>
    /// Gets the current number of jobs waiting to be processed.
    /// </summary>
    public int NumberOfPendingJobs => this.numberOfPendingJobs;

    private readonly ProcessJobDelegate? processJob;
    private readonly ObjectPool<TJob> freeJobs;
    private readonly ConcurrentQueue<TJob> pendingJobs;
    private AsyncPulseEvent? updateEvent = new();
    private int state;
    private int numberOfPendingJobs;
    private int numberOfTasks;

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
        if (!fireAndForget)
        {
            job._InitializeSynchronizationPrimitive();
        }

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
        {// Completed -> Initial, Aborted -> Initial
            job.State = ReusableJobState.Initial;
            if (job.FireAndForget)
            {
                job.FireAndForget = false;
            }
            else
            {
                job._ResetSynchronizationPrimitive();
            }

            job.OnReturnToPool();
            this.freeJobs.Return(job);
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

        job.State = ReusableJobState.Pending;
        Interlocked.Increment(ref this.numberOfPendingJobs);
        this.pendingJobs.Enqueue(job);
        this.updateEvent?.Pulse();

        if (this.State == ReusableJobWorkerState.Terminated)
        {
            job.State = ReusableJobState.Aborted;
            job._SetSynchronizationPrimitive();
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
        if (this.disposed)
        {
            throw new ObjectDisposedException(this.GetType().Name);
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
            else if (this.disposed)
            {
                return false;
            }
            else if (this.State == ReusableJobWorkerState.Terminated || this.IsTerminated)
            {
                return false;
            }

            var delayMilliseconds = DelayMilliseconds;
            if (millisecondsTimeout != Timeout.Infinite)
            {
                var elapsedMilliseconds = ((Stopwatch.GetTimestamp() - startTimestamp) * 1000) / Stopwatch.Frequency;
                long remainingMilliseconds = millisecondsTimeout - elapsedMilliseconds;

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

    protected virtual Task ProcessJob(TJob job)
    {
        return Task.CompletedTask;
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
